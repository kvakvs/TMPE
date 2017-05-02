﻿using ColossalFramework.Math;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TrafficManager.Manager;
using TrafficManager.State;
using TrafficManager.Traffic;
using TrafficManager.Util;
using Util;
using static TrafficManager.Geometry.LaneEndTransition;

namespace TrafficManager.Geometry {
	public class LaneEndGeometry {
		private static readonly Randomizer rand = new Randomizer();
		private static readonly ushort[] POW2MASKS = new ushort[] { 1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768 };

		public bool StartNode { get; private set; } = false;

		public LaneGeometry LaneGeometry { get; private set; } = null;

		public uint LaneId {
			get {
				return LaneGeometry.LaneId;		
			}
		}

		public int LaneIndex {
			get {
				return LaneGeometry.LaneIndex;
			}
		}

		public ushort SegmentId {
			get {
				return LaneGeometry.SegmentId;
			}
		}

		public ushort NodeId {
			get {
				return Constants.ServiceFactory.NetService.GetSegmentNodeId(SegmentId, StartNode);
			}
		}

		public bool Valid {
			get {
				if (!LaneGeometry.Valid || !Constants.ServiceFactory.NetService.IsNodeValid(NodeId)) {
					return false;
				}

				if (! IsIncoming()) {
					return false;
				}

				return LaneGeometry.LaneInfo.CheckType(LaneGeometry.ROUTED_LANE_TYPES, LaneGeometry.ROUTED_VEHICLE_TYPES);
			}
		}

		public LaneEndTransition[] IncomingTransitions { get; private set; } = null;

		public override string ToString() {
			return $"[LaneEndGeometry (id {LaneId}, idx {LaneIndex} @ seg. {SegmentId} @ node {NodeId}, start? {StartNode})\n" +
				"\t" + $"Valid = {Valid}\n" +
				"\t" + $"IncomingTransitions = {(IncomingTransitions == null ? "<null>" : IncomingTransitions.ArrayToString())}\n" +
				"LaneGeometry]";
		}

		public LaneEndGeometry(LaneGeometry laneGeo, bool startNode) {
			LaneGeometry = laneGeo;
			StartNode = startNode;
		}

		private LaneEndGeometry() {

		}

		public int CountLaneConnections() {
			return LaneConnectionManager.Instance.CountConnections(LaneId, StartNode);
		}

		public bool IsConnected(uint otherLaneId) {
			return LaneConnectionManager.Instance.AreLanesConnected(LaneId, otherLaneId, StartNode);
		}

		public void Recalculate() {
			Reset();

			if (! Valid) {
				return;
			}

			NetInfo prevSegmentInfo = null;
			bool prevSegIsInverted = false;
			Constants.ServiceFactory.NetService.ProcessSegment(SegmentId, delegate (ushort prevSegId, ref NetSegment segment) {
				prevSegmentInfo = segment.Info;
				prevSegIsInverted = (segment.m_flags & NetSegment.Flags.Invert) != NetSegment.Flags.None;
				return true;
			});

			LaneEndTransition[] newIncomingTransitions = new LaneEndTransition[16*8];
			int numNewIncomingTransitions = 0;

			bool leftHandDrive = Constants.ServiceFactory.SimulationService.LeftHandDrive;
			bool nextIsStartNode = !StartNode;

			SegmentGeometry prevSegGeo = SegmentGeometry.Get(SegmentId);
			if (prevSegGeo == null) {
				Log.Error($"LaneEndGeometry.Recalculate: prevSegGeo for segment {SegmentId} is null");
				return;
			}
			SegmentEndGeometry prevEndGeo = prevSegGeo.GetEnd(nextIsStartNode);
			if (prevEndGeo == null) {
				return;
			}

			ushort prevSegmentId = SegmentId;
			ushort nextSegmentId = SegmentId; // start with u-turns
			ushort nextNodeId = prevEndGeo.NodeId();

			NetInfo.Lane prevLaneInfo = prevSegmentInfo.m_lanes[LaneIndex];
			int prevSimilarLaneCount = prevLaneInfo.m_similarLaneCount;
			int prevInnerSimilarLaneIndex = LaneGeometry.InnerSimilarIndex;
			int prevOuterSimilarLaneIndex = LaneGeometry.OuterSimilarIndex;

			bool nextIsJunction = false;
			bool nextIsTransition = false;
			bool nextHasTrafficLights = false;
			Constants.ServiceFactory.NetService.ProcessNode(nextNodeId, delegate (ushort nodeId, ref NetNode node) {
				nextIsJunction = (node.m_flags & NetNode.Flags.Junction) != NetNode.Flags.None;
				nextIsTransition = (node.m_flags & NetNode.Flags.Transition) != NetNode.Flags.None;
				nextHasTrafficLights = (node.m_flags & NetNode.Flags.TrafficLights) != NetNode.Flags.None;
				return true;
			});

			bool nextIsSimpleJunction = false;
			if (Options.highwayRules && !nextHasTrafficLights) {
				// determine if junction is a simple junction (highway rules only apply to simple junctions)
				nextIsSimpleJunction = NodeGeometry.Get(nextNodeId).IsSimpleJunction;
			}
			bool nextIsRealJunction = prevSegGeo.CountOtherSegments(nextIsStartNode) > 1;
			bool nextAreOnlyOneWayHighways = prevEndGeo.OnlyHighways;

			// determine if highway rules should be applied
			bool applyHighwayRules = Options.highwayRules && nextIsSimpleJunction && nextAreOnlyOneWayHighways && prevEndGeo.OutgoingOneWay && prevSegGeo.IsHighway();
			bool applyHighwayRulesAtJunction = applyHighwayRules && nextIsRealJunction;

			int totalIncomingLanes = 0; // running number of next incoming lanes (number is updated at each segment iteration)
			int totalOutgoingLanes = 0; // running number of next outgoing lanes (number is updated at each segment iteration)

			for (int k = 0; k < 8; ++k) {
				int outgoingVehicleLanes = 0;
				int incomingVehicleLanes = 0;

				if (nextSegmentId == 0) {
					break;
				}

				bool uturn = nextSegmentId == prevSegmentId;
				bool nextIsStartNodeOfNextSegment = false;
				bool nextSegIsInverted = false;
				NetInfo nextSegmentInfo = null;
				uint nextFirstLaneId = 0;
				Constants.ServiceFactory.NetService.ProcessSegment(nextSegmentId, delegate (ushort nextSegId, ref NetSegment segment) {
					nextSegmentInfo = segment.Info;
					nextIsStartNodeOfNextSegment = segment.m_startNode == nextNodeId;
					nextSegIsInverted = (segment.m_flags & NetSegment.Flags.Invert) != NetSegment.Flags.None;
					nextFirstLaneId = segment.m_lanes;
					return true;
				});

				SegmentGeometry nextSegGeo = SegmentGeometry.Get(nextSegmentId);
				if (nextSegGeo == null) {
					Log.Error($"LaneEndGeometry.Recalculate: nextSegGeo for seg. {nextSegmentId} is null");
					continue;
				}
				bool nextIsHighway = nextSegGeo.IsHighway();

				// determine next segment direction by evaluating the geometry information
				bool isIncomingRight = false;
				bool isIncomingStraight = false;
				bool isIncomingLeft = false;
				bool isIncomingTurn = false;
				bool isValid = true;

				if (nextSegmentId != prevSegmentId) {
					for (int j = 0; j < prevEndGeo.IncomingStraightSegments.Length; ++j) {
						if (prevEndGeo.IncomingStraightSegments[j] == nextSegmentId)
							isIncomingStraight = true;
					}

					if (!isIncomingStraight) {
						for (int j = 0; j < prevEndGeo.IncomingRightSegments.Length; ++j) {
							if (prevEndGeo.IncomingRightSegments[j] == nextSegmentId)
								isIncomingRight = true;
						}

						if (!isIncomingRight) {
							for (int j = 0; j < prevEndGeo.IncomingLeftSegments.Length; ++j) {
								if (prevEndGeo.IncomingLeftSegments[j] == nextSegmentId)
									isIncomingLeft = true;
							}

							if (!isIncomingLeft)
								isValid = false;
						}
					}
				} else {
					isIncomingTurn = true;
				}

				if (isValid) {
					NetInfo.Direction nextDir = nextIsStartNodeOfNextSegment ? NetInfo.Direction.Backward : NetInfo.Direction.Forward;
					NetInfo.Direction nextDir2 = !nextSegIsInverted ? nextDir : NetInfo.InvertDirection(nextDir);

					byte curLaneI = 0; // current array index
					uint curLaneId = nextFirstLaneId;
					byte nextLaneIndex = 0;
					ushort compatibleOuterSimilarIndexesMask = 0;
					bool hasLaneConnections = false; // true if any lanes are connected by the lane connection tool
					LaneEndTransition[] nextCompatibleIncomingTransitions = new LaneEndTransition[nextSegmentInfo.m_lanes.Length];
					int numNextCompatibleIncomingTransitions = 0;

					IDictionary<int, int> indexByOuterSimilarIndex = new TinyDictionary<int, int>();

					while (nextLaneIndex < nextSegmentInfo.m_lanes.Length && curLaneId != 0u) {
						// determine valid lanes based on lane arrows
						NetInfo.Lane nextLaneInfo = nextSegmentInfo.m_lanes[nextLaneIndex];

						uint nextLaneId = 0u;
						NetLane.Flags curLaneFlags = NetLane.Flags.None;
						Constants.ServiceFactory.NetService.ProcessLane(curLaneId, delegate (uint lId, ref NetLane lane) {
							nextLaneId = lane.m_nextLane;
							curLaneFlags = (NetLane.Flags)lane.m_flags;
							return true;
						});

						if (nextLaneInfo.CheckType(LaneGeometry.ROUTED_LANE_TYPES, LaneGeometry.ROUTED_VEHICLE_TYPES)) { // is compatible lane
							if ((byte)(nextLaneInfo.m_finalDirection & nextDir2) != 0) { // is incoming lane
								++incomingVehicleLanes;
								LaneEndGeometry nextLaneEndGeo = nextSegGeo.GetLane(nextLaneIndex).GetEnd(nextIsStartNodeOfNextSegment);

								// calculate current similar lane index starting from outer lane
								int nextOuterSimilarLaneIndex = nextLaneEndGeo.LaneGeometry.OuterSimilarIndex;
								int nextInnerSimilarLaneIndex = nextLaneEndGeo.LaneGeometry.InnerSimilarIndex;
								bool isCompatibleLane = false;
								LaneEndTransitionType transitionType = LaneEndTransitionType.Invalid;

								// check for lane connections
								bool nextHasOutgoingConnections = false;
								int nextNumOutgoingConnections = 0;
								bool nextIsConnectedWithPrev = true;
								if (Options.laneConnectorEnabled) {
									nextNumOutgoingConnections = nextLaneEndGeo.CountLaneConnections();
									nextHasOutgoingConnections = nextNumOutgoingConnections != 0;
									if (nextHasOutgoingConnections) {
										hasLaneConnections = true;
										nextIsConnectedWithPrev = nextLaneEndGeo.IsConnected(LaneId);
									}
								}

								if (nextHasOutgoingConnections) {
									// check for lane connections
									if (nextIsConnectedWithPrev) {
										isCompatibleLane = true;
										transitionType = LaneEndTransitionType.LaneConnection;
									}
								} else if (nextLaneInfo.CheckType(LaneGeometry.ROUTED_LANE_TYPES, LaneGeometry.ARROW_VEHICLE_TYPES)) {
									// check for lane arrows
									bool hasLeftArrow = false;
									bool hasRightArrow = false;
									bool hasForwardArrow = false;
									if (!nextHasOutgoingConnections) {
										hasLeftArrow = (curLaneFlags & NetLane.Flags.Left) == NetLane.Flags.Left;
										hasRightArrow = (curLaneFlags & NetLane.Flags.Right) == NetLane.Flags.Right;
										hasForwardArrow = (curLaneFlags & NetLane.Flags.Forward) != NetLane.Flags.None || (curLaneFlags & NetLane.Flags.LeftForwardRight) == NetLane.Flags.None;
									}

									if (applyHighwayRules || // highway rules enabled
											(isIncomingRight && hasLeftArrow) || // valid incoming right
											(isIncomingLeft && hasRightArrow) || // valid incoming left
											(isIncomingStraight && hasForwardArrow) || // valid incoming straight
											(isIncomingTurn && ((leftHandDrive && hasRightArrow) || (!leftHandDrive && hasLeftArrow)))) { // valid turning lane
										isCompatibleLane = true;
										transitionType = LaneEndTransitionType.LaneArrow;
									} else {
										// lane can be used by all vehicles that may disregard lane arrows
										transitionType = LaneEndTransitionType.Relaxed;
										newIncomingTransitions[numNewIncomingTransitions++] = new LaneEndTransition(nextLaneEndGeo, this, transitionType, GlobalConfig.Instance.IncompatibleLaneDistance);
									}
								} else {
									// routed vehicle that does not follow lane arrows (e.g. trams, trains)
									transitionType = LaneEndTransitionType.Relaxed;
									newIncomingTransitions[numNewIncomingTransitions++] = new LaneEndTransition(nextLaneEndGeo, this, transitionType);
								}

								if (isCompatibleLane) {
									nextCompatibleIncomingTransitions[numNextCompatibleIncomingTransitions++] = new LaneEndTransition(nextLaneEndGeo, this, transitionType);
									indexByOuterSimilarIndex[nextOuterSimilarLaneIndex] = curLaneI;
									compatibleOuterSimilarIndexesMask |= POW2MASKS[nextOuterSimilarLaneIndex];

									++curLaneI;
								}
							} else {
								++outgoingVehicleLanes;
							}
						}

						curLaneId = nextLaneId;
						++nextLaneIndex;
					} // foreach lane

					if (numNextCompatibleIncomingTransitions > 0) {
						// we found compatible lanes

						// enable highway rules only at junctions or at simple lane merging/splitting points
						int laneDiff = numNextCompatibleIncomingTransitions - prevSimilarLaneCount;
						bool applyHighwayRulesAtSegment = applyHighwayRules && (applyHighwayRulesAtJunction || Math.Abs(laneDiff) == 1);

						if (!hasLaneConnections && applyHighwayRulesAtSegment) {
							// apply highway rules at transitions & junctions

							if (applyHighwayRulesAtJunction) {
								// we reached a highway junction where more than two segments are connected to each other
								LaneEndTransition nextLaneEndTransition = null;

								int numLanesSeen = Math.Max(totalIncomingLanes, totalOutgoingLanes); // number of lanes that were processed in earlier segment iterations (either all incoming or all outgoing)
								int nextInnerSimilarIndex;

								if (totalOutgoingLanes > 0) {
									// lane splitting at junction
									nextInnerSimilarIndex = prevInnerSimilarLaneIndex + numLanesSeen;
								} else {
									// lane merging at junction
									nextInnerSimilarIndex = prevInnerSimilarLaneIndex - numLanesSeen;
								}

								if (nextInnerSimilarIndex >= 0 && nextInnerSimilarIndex < numNextCompatibleIncomingTransitions) {
									// enough lanes available
									nextLaneEndTransition = FindLaneByInnerIndex(nextCompatibleIncomingTransitions, nextInnerSimilarIndex);
								} else {
									// Highway lanes "failed". Too few lanes at prevSegment or nextSegment.
									if (nextInnerSimilarIndex < 0) {
										// lane merging failed (too many incoming lanes)
										if (totalIncomingLanes >= prevSimilarLaneCount) {
											// there have already been explored more incoming lanes than outgoing lanes on the previous segment. Allow the current segment to also join the big merging party. What a fun!
											nextLaneEndTransition = FindLaneByOuterIndex(nextCompatibleIncomingTransitions, prevOuterSimilarLaneIndex);
										}
									} else if (totalOutgoingLanes >= numNextCompatibleIncomingTransitions) {
										// there have already been explored more outgoing lanes than incoming lanes on the previous segment. Also allow vehicles to go to the current segment.
										nextLaneEndTransition = FindLaneByOuterIndex(nextCompatibleIncomingTransitions, 0);
									}
								}

								// If nextLaneEndTransition is still null here, then highways rules really cannot handle this situation (that's ok).

								if (nextLaneEndTransition != null) {
									// go to matched lane

									// update highway mode lane arrows
									if (nextLaneEndTransition.SourceLaneEnd.CountLaneConnections() > 0) {
										Flags.removeHighwayLaneArrowFlags(nextLaneEndTransition.SourceLaneEnd.LaneId);
									} else if (applyHighwayRulesAtSegment) {
										Flags.LaneArrows? prevHighwayArrows = Flags.getHighwayLaneArrowFlags(nextLaneEndTransition.SourceLaneEnd.LaneId);
										Flags.LaneArrows newHighwayArrows = Flags.LaneArrows.None;
										if (prevHighwayArrows != null)
											newHighwayArrows = (Flags.LaneArrows)prevHighwayArrows;
										if (isIncomingRight)
											newHighwayArrows |= Flags.LaneArrows.Left;
										else if (isIncomingLeft)
											newHighwayArrows |= Flags.LaneArrows.Right;
										else if (isIncomingStraight)
											newHighwayArrows |= Flags.LaneArrows.Forward;

										if (newHighwayArrows != prevHighwayArrows && newHighwayArrows != Flags.LaneArrows.None)
											Flags.setHighwayLaneArrowFlags(nextLaneEndTransition.SourceLaneEnd.LaneId, newHighwayArrows, false);
									}

									newIncomingTransitions[numNewIncomingTransitions++] = nextLaneEndTransition;
								}
							} else {
								/* we reached a simple highway transition where lane splits or merges take place.
									this is guaranteed to be a simple lane splitting/merging point: the number of lanes is guaranteed to differ by 1
									due to:
									applyHighwayRulesAtSegment := applyHighwayRules && (applyHighwayRulesAtJunction || Math.Abs(laneDiff) == 1) [see above],
									applyHighwayRules == true,
									applyHighwayRulesAtSegment == true,
									applyHighwayRulesAtJunction == false
									=>
									true && (false || Math.Abs(laneDiff) == 1) == Math.Abs(laneDiff) == 1
								*/

								int minNextCompatibleOuterSimilarIndex = -1;
								int maxNextCompatibleOuterSimilarIndex = -1;

								if (laneDiff == 1) {
									// simple lane merge
									if (prevOuterSimilarLaneIndex == 0) {
										// merge outer lane
										minNextCompatibleOuterSimilarIndex = 0;
										maxNextCompatibleOuterSimilarIndex = 1;
									} else {
										// other lanes stay + 1
										minNextCompatibleOuterSimilarIndex = maxNextCompatibleOuterSimilarIndex = (short)(prevOuterSimilarLaneIndex + 1);
									}
								} else { // diff == -1
										 // simple lane split
									if (prevOuterSimilarLaneIndex <= 1) {
										// split outer lane
										minNextCompatibleOuterSimilarIndex = maxNextCompatibleOuterSimilarIndex = 0;
									} else {
										// other lanes stay - 1
										minNextCompatibleOuterSimilarIndex = maxNextCompatibleOuterSimilarIndex = (short)(prevOuterSimilarLaneIndex - 1);
									}
								}

								// explore lanes
								for (int nextCompatibleOuterSimilarIndex = minNextCompatibleOuterSimilarIndex; nextCompatibleOuterSimilarIndex <= maxNextCompatibleOuterSimilarIndex; ++nextCompatibleOuterSimilarIndex) {
									LaneEndTransition nextLaneEndTransition = FindLaneWithMaxOuterIndex(nextCompatibleIncomingTransitions, nextCompatibleOuterSimilarIndex);

									if (nextLaneEndTransition == null) {
										continue;
									}

									newIncomingTransitions[numNewIncomingTransitions++] = nextLaneEndTransition;
								}
							}
						} else {
							/*
							 * This is
							 *    1. a highway junction or lane splitting/merging point with lane connections or
							 *    2. a city or highway lane continuation point
							 *    3. a city junction
							 *  with multiple or a single target lane: Perform lane matching
							 */

							// min/max compatible outer similar lane indices
							int minNextCompatibleOuterSimilarIndex = -1;
							int maxNextCompatibleOuterSimilarIndex = -1;
							if (uturn) {
								// force u-turns to happen on the innermost lane
								minNextCompatibleOuterSimilarIndex = maxNextCompatibleOuterSimilarIndex = (short)(numNextCompatibleIncomingTransitions - 1);
							} else if (nextIsRealJunction) {
								// at junctions: try to match distinct lanes
								if (numNextCompatibleIncomingTransitions > prevSimilarLaneCount && prevOuterSimilarLaneIndex == prevSimilarLaneCount - 1) {
									// merge inner lanes
									minNextCompatibleOuterSimilarIndex = prevOuterSimilarLaneIndex;
									maxNextCompatibleOuterSimilarIndex = (short)(numNextCompatibleIncomingTransitions - 1);
								} else {
									// 1-to-n (lane splitting is done by FindCompatibleLane), 1-to-1 (direct lane matching)
									minNextCompatibleOuterSimilarIndex = prevOuterSimilarLaneIndex;
									maxNextCompatibleOuterSimilarIndex = prevOuterSimilarLaneIndex;
								}

								bool mayChangeLanes = isIncomingStraight && Flags.getStraightLaneChangingAllowed(nextSegmentId, nextIsStartNode);

								if (!mayChangeLanes) {
									bool prevHasBusLane = prevSegGeo.HasBusLane();
									bool nextHasBusLane = nextSegGeo.HasBusLane();
									if (nextHasBusLane && !prevHasBusLane) {
										// allow vehicles on the bus lane AND on the next lane to merge on this lane
										maxNextCompatibleOuterSimilarIndex = (short)Math.Min(numNextCompatibleIncomingTransitions - 1, maxNextCompatibleOuterSimilarIndex + 1);
									} else if (!nextHasBusLane && prevHasBusLane) {
										// allow vehicles to enter the bus lane
										minNextCompatibleOuterSimilarIndex = (short)Math.Max(0, minNextCompatibleOuterSimilarIndex - 1);
									}
								} else {
									// vehicles may change lanes when going straight
									minNextCompatibleOuterSimilarIndex = (short)Math.Max(0, minNextCompatibleOuterSimilarIndex - 1);
									maxNextCompatibleOuterSimilarIndex = (short)Math.Min(numNextCompatibleIncomingTransitions - 1, maxNextCompatibleOuterSimilarIndex + 1);
								}
							} else {
								// lane merging/splitting
								//HandleLaneMergesAndSplits(ref item, nextSegmentId, prevOuterSimilarLaneIndex, nextCompatibleLaneCount, prevSimilarLaneCount, out minNextCompatibleOuterSimilarIndex, out maxNextCompatibleOuterSimilarIndex);

								bool sym1 = (prevSimilarLaneCount & 1) == 0; // mod 2 == 0
								bool sym2 = (numNextCompatibleIncomingTransitions & 1) == 0; // mod 2 == 0
								if (prevSimilarLaneCount < numNextCompatibleIncomingTransitions) {
									// lane merging
									if (sym1 == sym2) {
										// merge outer lanes
										short a = (short)((byte)(numNextCompatibleIncomingTransitions - prevSimilarLaneCount) >> 1); // nextCompatibleLaneCount - prevSimilarLaneCount is always > 0
										if (prevSimilarLaneCount == 1) {
											minNextCompatibleOuterSimilarIndex = 0;
											maxNextCompatibleOuterSimilarIndex = (short)(numNextCompatibleIncomingTransitions - 1); // always >=0
										} else if (prevOuterSimilarLaneIndex == 0) {
											minNextCompatibleOuterSimilarIndex = 0;
											maxNextCompatibleOuterSimilarIndex = a;
										} else if (prevOuterSimilarLaneIndex == prevSimilarLaneCount - 1) {
											minNextCompatibleOuterSimilarIndex = (short)(prevOuterSimilarLaneIndex + a);
											maxNextCompatibleOuterSimilarIndex = (short)(numNextCompatibleIncomingTransitions - 1); // always >=0
										} else {
											minNextCompatibleOuterSimilarIndex = maxNextCompatibleOuterSimilarIndex = (short)(prevOuterSimilarLaneIndex + a);
										}
									} else {
										// criss-cross merge
										short a = (short)((byte)(numNextCompatibleIncomingTransitions - prevSimilarLaneCount - 1) >> 1); // nextCompatibleLaneCount - prevSimilarLaneCount - 1 is always >= 0
										short b = (short)((byte)(numNextCompatibleIncomingTransitions - prevSimilarLaneCount + 1) >> 1); // nextCompatibleLaneCount - prevSimilarLaneCount + 1 is always >= 2
										if (prevSimilarLaneCount == 1) {
											minNextCompatibleOuterSimilarIndex = 0;
											maxNextCompatibleOuterSimilarIndex = (short)(numNextCompatibleIncomingTransitions - 1); // always >=0
										} else if (prevOuterSimilarLaneIndex == 0) {
											minNextCompatibleOuterSimilarIndex = 0;
											maxNextCompatibleOuterSimilarIndex = b;
										} else if (prevOuterSimilarLaneIndex == prevSimilarLaneCount - 1) {
											minNextCompatibleOuterSimilarIndex = (short)(prevOuterSimilarLaneIndex + a);
											maxNextCompatibleOuterSimilarIndex = (short)(numNextCompatibleIncomingTransitions - 1); // always >=0
										} else if (rand.Int32(0, 1) == 0) {
											minNextCompatibleOuterSimilarIndex = maxNextCompatibleOuterSimilarIndex = (short)(prevOuterSimilarLaneIndex + a);
										} else {
											minNextCompatibleOuterSimilarIndex = maxNextCompatibleOuterSimilarIndex = (short)(prevOuterSimilarLaneIndex + b);
										}
									}
								} else if (prevSimilarLaneCount == numNextCompatibleIncomingTransitions) {
									minNextCompatibleOuterSimilarIndex = maxNextCompatibleOuterSimilarIndex = prevOuterSimilarLaneIndex;
								} else {
									// at lane splits: distribute traffic evenly (1-to-n, n-to-n)										
									// prevOuterSimilarIndex is always > nextCompatibleLaneCount
									if (sym1 == sym2) {
										// split outer lanes
										short a = (short)((byte)(prevSimilarLaneCount - numNextCompatibleIncomingTransitions) >> 1); // prevSimilarLaneCount - nextCompatibleLaneCount is always > 0
										minNextCompatibleOuterSimilarIndex = maxNextCompatibleOuterSimilarIndex = (short)(prevOuterSimilarLaneIndex - a); // a is always <= prevSimilarLaneCount
									} else {
										// split outer lanes, criss-cross inner lanes 
										short a = (short)((byte)(prevSimilarLaneCount - numNextCompatibleIncomingTransitions - 1) >> 1); // prevSimilarLaneCount - nextCompatibleLaneCount - 1 is always >= 0

										minNextCompatibleOuterSimilarIndex = (a - 1 >= prevOuterSimilarLaneIndex) ? (short)0 : (short)(prevOuterSimilarLaneIndex - a - 1);
										maxNextCompatibleOuterSimilarIndex = (a >= prevOuterSimilarLaneIndex) ? (short)0 : (short)(prevOuterSimilarLaneIndex - a);
									}
								}
								if (minNextCompatibleOuterSimilarIndex > numNextCompatibleIncomingTransitions - 1) {
									minNextCompatibleOuterSimilarIndex = (short)(numNextCompatibleIncomingTransitions - 1);
								}
								if (maxNextCompatibleOuterSimilarIndex > numNextCompatibleIncomingTransitions - 1) {
									maxNextCompatibleOuterSimilarIndex = (short)(numNextCompatibleIncomingTransitions - 1);
								}

								if (minNextCompatibleOuterSimilarIndex > maxNextCompatibleOuterSimilarIndex) {
									minNextCompatibleOuterSimilarIndex = maxNextCompatibleOuterSimilarIndex;
								}
							}

							// find best matching lane(s)
							int minIndex = minNextCompatibleOuterSimilarIndex;
							int maxIndex = maxNextCompatibleOuterSimilarIndex;
							if (hasLaneConnections) {
								minIndex = 0;
								maxIndex = numNextCompatibleIncomingTransitions - 1;
							}

							for (int nextCompatibleOuterSimilarIndex = minIndex; nextCompatibleOuterSimilarIndex <= maxIndex; ++nextCompatibleOuterSimilarIndex) {
								LaneEndTransition nextLaneEndTransition = FindLaneWithMaxOuterIndex(nextCompatibleIncomingTransitions, nextCompatibleOuterSimilarIndex);

								if (nextLaneEndTransition == null) {
									continue;
								}

								if (hasLaneConnections) {
									int nextNumConnections = nextLaneEndTransition.SourceLaneEnd.CountLaneConnections();
									bool nextIsConnectedWithPrev = nextLaneEndTransition.SourceLaneEnd.IsConnected(LaneId);
									if (nextCompatibleOuterSimilarIndex < minNextCompatibleOuterSimilarIndex || nextCompatibleOuterSimilarIndex > maxNextCompatibleOuterSimilarIndex) {
										if (nextNumConnections == 0 || !nextIsConnectedWithPrev) {
											continue; // disregard lane since it is not connected to previous lane
										}
									} else {
										if (nextNumConnections != 0 && !nextIsConnectedWithPrev) {
											continue; // disregard lane since it is not connected to previous lane but has outgoing connections
										}
									}
								}

								byte compatibleLaneDist = 0;
								if (uturn) {
									compatibleLaneDist = (byte)GlobalConfig.Instance.UturnLaneDistance;
								} else if (!hasLaneConnections) {
									if ((compatibleOuterSimilarIndexesMask & POW2MASKS[nextCompatibleOuterSimilarIndex]) != 0) {
										if (!nextIsRealJunction && numNextCompatibleIncomingTransitions == prevSimilarLaneCount) {
											int relLaneDist = nextLaneEndTransition.SourceLaneEnd.LaneGeometry.OuterSimilarIndex - prevOuterSimilarLaneIndex; // relative lane distance (positive: change to more outer lane, negative: change to more inner lane)
											compatibleLaneDist = (byte)Math.Abs(relLaneDist);
										}
									} else {
										compatibleLaneDist = GlobalConfig.Instance.IncompatibleLaneDistance;
									}
								}

								nextLaneEndTransition.LaneDistance = compatibleLaneDist;
								newIncomingTransitions[numNewIncomingTransitions++] = nextLaneEndTransition;
							} // foreach lane
						}
					}
				}

				Constants.ServiceFactory.NetService.ProcessSegment(nextSegmentId, delegate (ushort nextSegId, ref NetSegment segment) {
					if (Constants.ServiceFactory.SimulationService.LeftHandDrive) {
						nextSegmentId = segment.GetLeftSegment(nextNodeId);
					} else {
						nextSegmentId = segment.GetRightSegment(nextNodeId);
					}
					return true;
				});

				if (nextSegmentId != prevSegmentId) {
					totalIncomingLanes += incomingVehicleLanes;
					totalOutgoingLanes += outgoingVehicleLanes;
				}
			} // foreach segment

			LaneEndTransition[] tmp = new LaneEndTransition[numNewIncomingTransitions];
			Array.Copy(newIncomingTransitions, tmp, numNewIncomingTransitions);
			IncomingTransitions = tmp;
		}

		private static LaneEndTransition FindLaneWithMaxOuterIndex(LaneEndTransition[] laneEndConnections, int outerLaneIndex) {
			return laneEndConnections.Where(conn => conn != null && conn.SourceLaneEnd.LaneGeometry.OuterSimilarIndex <= outerLaneIndex).OrderByDescending(conn => conn.SourceLaneEnd.LaneGeometry.OuterSimilarIndex).FirstOrDefault();
		}

		private static LaneEndTransition FindLaneByOuterIndex(LaneEndTransition[] laneEndConnections, int outerLaneIndex) {
			return laneEndConnections.Where(conn => conn != null && conn.SourceLaneEnd.LaneGeometry.OuterSimilarIndex == outerLaneIndex).FirstOrDefault();
		}

		private static LaneEndTransition FindLaneByInnerIndex(LaneEndTransition[] laneEndConnections, int innerLaneIndex) {
			return laneEndConnections.Where(conn => conn != null && conn.SourceLaneEnd.LaneGeometry.InnerSimilarIndex == innerLaneIndex).FirstOrDefault();
		}

		public void Reset() {
			IncomingTransitions = null;
		}

		private bool IsOutgoing() {
			return IsIncomingOutgoing(false);
		}

		private bool IsIncoming() {
			return IsIncomingOutgoing(true);
		}

		private bool IsIncomingOutgoing(bool incoming) {
			bool segIsInverted = false;
			Constants.ServiceFactory.NetService.ProcessSegment(SegmentId, delegate (ushort segmentId, ref NetSegment segment) {
				segIsInverted = (segment.m_flags & NetSegment.Flags.Invert) != NetSegment.Flags.None;
				return true;
			});

			NetInfo.Direction dir = StartNode ? NetInfo.Direction.Forward : NetInfo.Direction.Backward;
			dir = incoming ^ segIsInverted ? NetInfo.InvertDirection(dir) : dir;

			NetInfo.Direction finalDir = NetInfo.Direction.None;
			Constants.ServiceFactory.NetService.ProcessSegment(SegmentId, delegate (ushort segmentId, ref NetSegment segment) {
				finalDir = segment.Info.m_lanes[LaneIndex].m_finalDirection;
				return true;
			});

			return (finalDir & dir) != NetInfo.Direction.None;
		}
	}
}
