﻿namespace TrafficManager.Manager.Impl {
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using API.Manager;
    using API.Traffic.Enums;
    using ColossalFramework;
    using CSUtil.Commons;
    using State.ConfigData;
    using State;
    using Traffic.Data;
    using Traffic.Enums;

    public class RoutingManager
        : AbstractGeometryObservingManager,
          IRoutingManager
    {
        public static readonly RoutingManager Instance = new RoutingManager();

        private const NetInfo.LaneType ROUTED_LANE_TYPES =
            NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle;

        private const VehicleInfo.VehicleType ROUTED_VEHICLE_TYPES =
            VehicleInfo.VehicleType.Car | VehicleInfo.VehicleType.Metro |
            VehicleInfo.VehicleType.Train | VehicleInfo.VehicleType.Tram |
            VehicleInfo.VehicleType.Monorail;

        private const VehicleInfo.VehicleType ARROW_VEHICLE_TYPES = VehicleInfo.VehicleType.Car;

        private const byte MAX_NUM_TRANSITIONS = 64;

        /// <summary>
        /// Structs for path-finding that contain required segment-related routing data
        /// </summary>
        public SegmentRoutingData[] SegmentRoutings { get; } =
            new SegmentRoutingData[NetManager.MAX_SEGMENT_COUNT];

        /// <summary>
        /// Structs for path-finding that contain required lane-end-related backward routing data.
        /// Index:
        ///    [0 .. NetManager.MAX_LANE_COUNT-1]: lane ends at start node
        ///    [NetManager.MAX_LANE_COUNT .. 2*NetManger.MAX_LANE_COUNT-1]: lane ends at end node
        /// </summary>
        public LaneEndRoutingData[] LaneEndBackwardRoutings { get; } =
            new LaneEndRoutingData[(uint)NetManager.MAX_LANE_COUNT * 2u];

        /// <summary>
        /// Structs for path-finding that contain required lane-end-related forward routing data.
        /// Index:
        ///    [0 .. NetManager.MAX_LANE_COUNT-1]: lane ends at start node
        ///    [NetManager.MAX_LANE_COUNT .. 2*NetManger.MAX_LANE_COUNT-1]: lane ends at end node
        /// </summary>
        public LaneEndRoutingData[] LaneEndForwardRoutings { get; } =
            new LaneEndRoutingData[(uint)NetManager.MAX_LANE_COUNT * 2u];

        private bool segmentsUpdated;

        private readonly ulong[] updatedSegmentBuckets = new ulong[576];

        private readonly object updateLock = new object();

        protected override void InternalPrintDebugInfo() {
            base.InternalPrintDebugInfo();
            string buf = $"Segment routings:\n";

            for (var i = 0; i < SegmentRoutings.Length; ++i) {
                if (!Services.NetService.IsSegmentValid((ushort)i)) {
                    continue;
                }

                buf += $"Segment {i}: {SegmentRoutings[i]}\n";
            }

            buf += $"\nLane end backward routings:\n";

            for (uint laneId = 0; laneId < NetManager.MAX_LANE_COUNT; ++laneId) {
                if (!Services.NetService.IsLaneValid(laneId)) {
                    continue;
                }

                buf += $"Lane {laneId} @ start: {LaneEndBackwardRoutings[GetLaneEndRoutingIndex(laneId, true)]}\n";
                buf += $"Lane {laneId} @ end: {LaneEndBackwardRoutings[GetLaneEndRoutingIndex(laneId, false)]}\n";
            }

            buf += $"\nLane end forward routings:\n";

            for (uint laneId = 0; laneId < NetManager.MAX_LANE_COUNT; ++laneId) {
                if (!Services.NetService.IsLaneValid(laneId)) {
                    continue;
                }

                buf += $"Lane {laneId} @ start: {LaneEndForwardRoutings[GetLaneEndRoutingIndex(laneId, true)]}\n";
                buf += $"Lane {laneId} @ end: {LaneEndForwardRoutings[GetLaneEndRoutingIndex(laneId, false)]}\n";
            }

            Log._Debug(buf);
        }

        private RoutingManager() { }

        public void SimulationStep() {
            if (!segmentsUpdated || Singleton<NetManager>.instance.m_segmentsUpdated
                                 || Singleton<NetManager>.instance.m_nodesUpdated) {
                // TODO maybe refactor NetManager use (however this could influence performance)
                return;
            }

            try {
                Monitor.Enter(updateLock);
                segmentsUpdated = false;

                int len = updatedSegmentBuckets.Length;
                for (int i = 0; i < len; i++) {
                    ulong segMask = updatedSegmentBuckets[i];

                    if (segMask != 0uL) {
                        for (var m = 0; m < 64; m++) {

                            if ((segMask & 1uL << m) != 0uL) {
                                var segmentId = (ushort)(i << 6 | m);
                                RecalculateSegment(segmentId);
                            }
                        }

                        updatedSegmentBuckets[i] = 0;
                    }
                }
            }
            finally {
                Monitor.Exit(updateLock);
            }
        }

        public void RequestFullRecalculation() {
            try {
                Monitor.Enter(updateLock);

                for (uint segmentId = 0; segmentId < NetManager.MAX_SEGMENT_COUNT; ++segmentId) {
                    updatedSegmentBuckets[segmentId >> 6] |= 1uL << (int)(segmentId & 63);
                }

                Flags.clearHighwayLaneArrows();
                segmentsUpdated = true;

                if (Services.SimulationService.SimulationPaused ||
                    Services.SimulationService.ForcedSimulationPaused) {
                    SimulationStep();
                }
            }
            finally {
                Monitor.Exit(updateLock);
            }
        }

        public void RequestRecalculation(ushort segmentId, bool propagate = true) {
#if DEBUG
            bool logRouting = DebugSwitch.RoutingBasicLog.Get()
                         && (DebugSettings.SegmentId <= 0
                             || DebugSettings.SegmentId == segmentId);
#else
            const bool logRouting = false;
#endif
            if (logRouting) {
                Log._Debug($"RoutingManager.RequestRecalculation({segmentId}, {propagate}) called.");
            }

            try {
                Monitor.Enter(updateLock);

                updatedSegmentBuckets[segmentId >> 6] |= 1uL << (int)(segmentId & 63);
                ResetIncomingHighwayLaneArrows(segmentId);
                segmentsUpdated = true;
            }
            finally {
                Monitor.Exit(updateLock);
            }

            if (propagate) {
                ushort startNodeId = Services.NetService.GetSegmentNodeId(segmentId, true);
                Services.NetService.IterateNodeSegments(
                    startNodeId,
                    (ushort otherSegmentId, ref NetSegment otherSeg) => {
                        RequestRecalculation(otherSegmentId, false);
                        return true;
                    });

                ushort endNodeId = Services.NetService.GetSegmentNodeId(segmentId, false);
                Services.NetService.IterateNodeSegments(
                    endNodeId,
                    (ushort otherSegmentId, ref NetSegment otherSeg) => {
                        RequestRecalculation(otherSegmentId, false);
                        return true;
                    });
            }
        }

        protected void RecalculateAll() {
#if DEBUGROUTING
            bool logRouting = DebugSwitch.RoutingBasicLog.Get();
            Log._Debug($"RoutingManager.RecalculateAll: called");
#endif
            Flags.clearHighwayLaneArrows();
            for (uint segmentId = 0; segmentId < NetManager.MAX_SEGMENT_COUNT; ++segmentId) {
                try {
                    RecalculateSegment((ushort)segmentId);
                }
                catch (Exception e) {
                    Log.Error($"An error occurred while calculating routes for segment {segmentId}: {e}");
                }
            }
        }

        protected void RecalculateSegment(ushort segmentId) {
#if DEBUGROUTING
            bool debug = DebugSwitch.RoutingBasicLog.Get() &&
                         (DebugSettings.SegmentId <= 0 || DebugSettings.SegmentId == segmentId);
            if (debug) {
                Log._Debug($"RoutingManager.RecalculateSegment({segmentId}) called.");
            }
#endif

            if (!Services.NetService.IsSegmentValid(segmentId)) {
#if DEBUGROUTING
                if (debug) {
                    Log._Debug($"RoutingManager.RecalculateSegment({segmentId}): " +
                               "Segment is invalid. Skipping recalculation");
                }
#endif
                return;
            }

            RecalculateSegmentRoutingData(segmentId);

            Services.NetService.IterateSegmentLanes(
                segmentId,
                (uint laneId,
                 ref NetLane lane,
                 NetInfo.Lane laneInfo,
                 ushort segId,
                 ref NetSegment segment,
                 byte laneIndex) => {
                    RecalculateLaneEndRoutingData(segmentId, laneIndex, laneId, true);
                    RecalculateLaneEndRoutingData(segmentId, laneIndex, laneId, false);

                    return true;
                });
        }

        protected void ResetIncomingHighwayLaneArrows(ushort segmentId) {
            ushort[] nodeIds = new ushort[2];
            Services.NetService.ProcessSegment(
                segmentId,
                (ushort segId, ref NetSegment segment) => {
                    nodeIds[0] = segment.m_startNode;
                    nodeIds[1] = segment.m_endNode;
                    return true;
                });

#if DEBUGROUTING
            bool logRouting = DebugSwitch.RoutingBasicLog.Get()
                              && (DebugSettings.SegmentId <= 0
                                  || DebugSettings.SegmentId == segmentId);
#else
            const bool logRouting = false;
#endif
            if (logRouting) {
                Log._Debug("RoutingManager.ResetRoutingData: Identify nodes connected to " +
                           $"{segmentId}: nodeIds={nodeIds.ArrayToString()}");
            }

            // reset highway lane arrows on all incoming lanes
            foreach (ushort nodeId in nodeIds) {
                if (nodeId == 0) {
                    continue;
                }

                Services.NetService.IterateNodeSegments(
                    nodeId,
                    (ushort segId, ref NetSegment segment) => {
                        if (segId == segmentId) {
                            return true;
                        }

                        Services.NetService.IterateSegmentLanes(
                            segId,
                            (uint laneId,
                             ref NetLane lane,
                             NetInfo.Lane laneInfo,
                             ushort sId,
                             ref NetSegment seg,
                             byte laneIndex) => {
                                if (IsIncomingLane(
                                    segId,
                                    seg.m_startNode == nodeId,
                                    laneIndex)) {
                                    Flags.removeHighwayLaneArrowFlags(laneId);
                                }

                                return true;
                            });
                        return true;
                    });
            }
        }

        protected void ResetRoutingData(ushort segmentId) {
#if DEBUGROUTING
            bool logRouting = DebugSwitch.RoutingBasicLog.Get()
                              && (DebugSettings.SegmentId <= 0
                                  || DebugSettings.SegmentId == segmentId);
            bool extendedLogRouting = DebugSwitch.Routing.Get()
                                      && (DebugSettings.SegmentId <= 0
                                          || DebugSettings.SegmentId == segmentId);
#else
            const bool logRouting = false;
            const bool extendedLogRouting = false;
#endif

            if (logRouting) {
                Log._Debug($"RoutingManager.ResetRoutingData: called for segment {segmentId}");
            }

            SegmentRoutings[segmentId].Reset();
            ResetIncomingHighwayLaneArrows(segmentId);

            Services.NetService.IterateSegmentLanes(
                segmentId,
                (uint laneId,
                 ref NetLane lane,
                 NetInfo.Lane laneInfo,
                 ushort segId,
                 ref NetSegment segment,
                 byte laneIndex) => {
#if DEBUGROUTING
                    if (extendedLogRouting) {
                        Log._Debug($"RoutingManager.ResetRoutingData: Resetting lane {laneId}, " +
                                   $"idx {laneIndex} @ seg. {segmentId}");
                    }
#endif
                    ResetLaneRoutings(laneId, true);
                    ResetLaneRoutings(laneId, false);

                    return true;
                });
        }

        protected void RecalculateSegmentRoutingData(ushort segmentId) {
#if DEBUGROUTING
            bool logRouting = DebugSwitch.RoutingBasicLog.Get()
                              && (DebugSettings.SegmentId <= 0
                                  || DebugSettings.SegmentId == segmentId);
            bool extendedLogRouting = DebugSwitch.Routing.Get()
                                      && (DebugSettings.SegmentId <= 0
                                          || DebugSettings.SegmentId == segmentId);
#else
            const bool logRouting = false;
            const bool extendedLogRouting = false;
#endif
            if (logRouting) {
                Log._Debug($"RoutingManager.RecalculateSegmentRoutingData: called for seg. {segmentId}");
            }

            SegmentRoutings[segmentId].Reset();

            IExtSegmentEndManager segEndMan = Constants.ManagerFactory.ExtSegmentEndManager;
            ExtSegment seg = Constants.ManagerFactory.ExtSegmentManager.ExtSegments[segmentId];
            ExtSegmentEnd startSegEnd = segEndMan.ExtSegmentEnds[segEndMan.GetIndex(segmentId, true)];
            ExtSegmentEnd endSegEnd = segEndMan.ExtSegmentEnds[segEndMan.GetIndex(segmentId, false)];

            SegmentRoutings[segmentId].highway = seg.highway;
            SegmentRoutings[segmentId].startNodeOutgoingOneWay = seg.oneWay && startSegEnd.outgoing;
            SegmentRoutings[segmentId].endNodeOutgoingOneWay = seg.oneWay && endSegEnd.outgoing;

#if DEBUGROUTING
            if (logRouting) {
                Log._Debug("RoutingManager.RecalculateSegmentRoutingData: Calculated routing " +
                           $"data for segment {segmentId}: {SegmentRoutings[segmentId]}");
            }
#endif
        }

        protected void RecalculateLaneEndRoutingData(ushort segmentId, int laneIndex, uint laneId, bool startNode) {
#if DEBUGROUTING
            bool logRouting = DebugSwitch.RoutingBasicLog.Get()
                              && (DebugSettings.SegmentId <= 0
                                  || DebugSettings.SegmentId == segmentId);
            bool extendedLogRouting = DebugSwitch.Routing.Get()
                                      && (DebugSettings.SegmentId <= 0
                                          || DebugSettings.SegmentId == segmentId);
#else
            const bool logRouting = false;
            const bool extendedLogRouting = false;
#endif
            if (logRouting) {
                Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, " +
                           $"{laneIndex}, {laneId}, {startNode}) called");
            }

            ResetLaneRoutings(laneId, startNode);

            if (!IsOutgoingLane(segmentId, startNode, laneIndex)) {
#if DEBUGROUTING
                if (extendedLogRouting) {
                    Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, " +
                               $"{laneIndex}, {laneId}, {startNode}): Lane is not an outgoing lane");
                }
#endif
                return;
            }

            NetInfo prevSegmentInfo = null;
            bool prevSegIsInverted = false;
            Constants.ServiceFactory.NetService.ProcessSegment(
                segmentId,
                (ushort prevSegId, ref NetSegment segment) => {
                    prevSegmentInfo = segment.Info;
                    prevSegIsInverted =
                        (segment.m_flags & NetSegment.Flags.Invert) !=
                        NetSegment.Flags.None;
                    return true;
                });

            bool leftHandDrive = Constants.ServiceFactory.SimulationService.LeftHandDrive;

            IExtSegmentEndManager segEndMan = Constants.ManagerFactory.ExtSegmentEndManager;
            ExtSegment prevSeg = Constants.ManagerFactory.ExtSegmentManager.ExtSegments[segmentId];
            ExtSegmentEnd prevEnd = segEndMan.ExtSegmentEnds[segEndMan.GetIndex(segmentId, startNode)];

            ushort prevSegmentId = segmentId;
            int prevLaneIndex = laneIndex;
            uint prevLaneId = laneId;
            ushort nextNodeId = prevEnd.nodeId;

            NetInfo.Lane prevLaneInfo = prevSegmentInfo.m_lanes[prevLaneIndex];
            if (!prevLaneInfo.CheckType(ROUTED_LANE_TYPES, ROUTED_VEHICLE_TYPES)) {
                return;
            }

            LaneEndRoutingData backwardRouting = new LaneEndRoutingData();
            backwardRouting.routed = true;

            int prevSimilarLaneCount = prevLaneInfo.m_similarLaneCount;
            int prevInnerSimilarLaneIndex = CalcInnerSimilarLaneIndex(prevSegmentId, prevLaneIndex);
            int prevOuterSimilarLaneIndex = CalcOuterSimilarLaneIndex(prevSegmentId, prevLaneIndex);
            bool prevHasBusLane = prevSeg.buslane;

            bool nextIsJunction = false;
            bool nextIsTransition = false;
            bool nextIsEndOrOneWayOut = false;
            bool nextHasTrafficLights = false;
            bool nextHasPrioritySigns =
                Constants.ManagerFactory.TrafficPriorityManager.HasNodePrioritySign(nextNodeId);
            bool nextIsRealJunction = false;
            ushort buildingId = 0;
            Constants.ServiceFactory.NetService.ProcessNode(
                nextNodeId,
                (ushort nodeId, ref NetNode node) => {
                    nextIsJunction =
                        (node.m_flags & NetNode.Flags.Junction) != NetNode.Flags.None;
                    nextIsTransition =
                        (node.m_flags & NetNode.Flags.Transition) != NetNode.Flags.None;
                    nextHasTrafficLights =
                        (node.m_flags & NetNode.Flags.TrafficLights) !=
                        NetNode.Flags.None;
                    nextIsEndOrOneWayOut =
                        (node.m_flags & (NetNode.Flags.End | NetNode.Flags.OneWayOut)) !=
                        NetNode.Flags.None;
                    nextIsRealJunction = node.CountSegments() >= 3;
                    buildingId = NetNode.FindOwnerBuilding(nextNodeId, 32f);
                    return true;
                });

            bool isTollBooth = false;
            if (buildingId != 0) {
                Constants.ServiceFactory.BuildingService.ProcessBuilding(
                    buildingId,
                    (ushort bId, ref Building building) => {
                        isTollBooth = building.Info.m_buildingAI is TollBoothAI;
                        return true;
                    });
            }

            bool nextIsSimpleJunction = false;
            bool nextIsSplitJunction = false;

            if (Options.highwayRules && !nextHasTrafficLights && !nextHasPrioritySigns) {
                // determine if junction is a simple junction (highway rules only apply to simple junctions)
                int numOutgoing = 0;
                int numIncoming = 0;

                for (int i = 0; i < 8; ++i) {
                    ushort segId = 0;
                    Constants.ServiceFactory.NetService.ProcessNode(
                        nextNodeId,
                        (ushort nId, ref NetNode node) => {
                            segId = node.GetSegment(i);
                            return true;
                        });

                    if (segId == 0) {
                        continue;
                    }

                    bool start = (bool)Constants.ServiceFactory.NetService.IsStartNode(segId, nextNodeId);
                    ExtSegmentEnd segEnd = segEndMan.ExtSegmentEnds[segEndMan.GetIndex(segId, start)];

                    if (segEnd.incoming) {
                        ++numIncoming;
                    }

                    if (segEnd.outgoing) {
                        ++numOutgoing;
                    }
                }

                nextIsSimpleJunction = numOutgoing == 1 || numIncoming == 1;
                nextIsSplitJunction = numOutgoing > 1;
            }

            // bool isNextRealJunction = prevSegGeo.CountOtherSegments(startNode) > 1;
            bool nextAreOnlyOneWayHighways =
                Constants.ManagerFactory.ExtSegmentEndManager.CalculateOnlyHighways(
                    prevEnd.segmentId,
                    prevEnd.startNode);

            // determine if highway rules should be applied
            bool onHighway = Options.highwayRules && nextAreOnlyOneWayHighways &&
                             prevEnd.outgoing && prevSeg.oneWay && prevSeg.highway;
            bool applyHighwayRules = onHighway && nextIsSimpleJunction;
            bool applyHighwayRulesAtJunction = applyHighwayRules && nextIsRealJunction;
            bool iterateViaGeometry = applyHighwayRulesAtJunction &&
                                      prevLaneInfo.CheckType(
                                          ROUTED_LANE_TYPES,
                                          ARROW_VEHICLE_TYPES);
            // start with u-turns at highway junctions
            ushort nextSegmentId = iterateViaGeometry ? segmentId : (ushort)0;

#if DEBUGROUTING
            if (extendedLogRouting) {
                Log._Debug(string.Format(
                    "RoutingManager.RecalculateLaneEndRoutingData({0}, {1}, {2}, {3}): " +
                    "prevSegment={4}. Starting exploration with nextSegment={5} @ nextNodeId={6} " +
                    "-- onHighway={7} applyHighwayRules={8} applyHighwayRulesAtJunction={9} " +
                    "Options.highwayRules={10} nextIsSimpleJunction={11} nextAreOnlyOneWayHighways={12} " +
                    "prevEndGeo.OutgoingOneWay={13} prevSegGeo.IsHighway()={14} iterateViaGeometry={15}",
                    segmentId, laneIndex, laneId, startNode, segmentId, nextSegmentId, nextNodeId,
                    onHighway, applyHighwayRules, applyHighwayRulesAtJunction, Options.highwayRules,
                    nextIsSimpleJunction, nextAreOnlyOneWayHighways, prevEnd.outgoing && prevSeg.oneWay,
                    prevSeg.highway, iterateViaGeometry));
                Log._Debug(string.Format(
                    "RoutingManager.RecalculateLaneEndRoutingData({0}, {1}, {2}, {3}): " +
                    "prevSegIsInverted={4} leftHandDrive={5}",
                    segmentId, laneIndex, laneId, startNode, prevSegIsInverted, leftHandDrive));
                Log._Debug(string.Format(
                    "RoutingManager.RecalculateLaneEndRoutingData({0}, {1}, {2}, {3}): " +
                    "prevSimilarLaneCount={4} prevInnerSimilarLaneIndex={5} prevOuterSimilarLaneIndex={6} " +
                    "prevHasBusLane={7}",
                    segmentId, laneIndex, laneId, startNode, prevSimilarLaneCount, prevInnerSimilarLaneIndex,
                    prevOuterSimilarLaneIndex, prevHasBusLane));
                Log._Debug(string.Format(
                    "RoutingManager.RecalculateLaneEndRoutingData({0}, {1}, {2}, {3}): nextIsJunction={4} " +
                    "nextIsEndOrOneWayOut={5} nextHasTrafficLights={6} nextIsSimpleJunction={7} " +
                    "nextIsSplitJunction={8} isNextRealJunction={9}",
                    segmentId, laneIndex, laneId, startNode, nextIsJunction, nextIsEndOrOneWayOut,
                    nextHasTrafficLights, nextIsSimpleJunction, nextIsSplitJunction, nextIsRealJunction));
                Log._Debug(string.Format(
                    "RoutingManager.RecalculateLaneEndRoutingData({0}, {1}, {2}, {3}): nextNodeId={4} " +
                    "buildingId={5} isTollBooth={6}",
                    segmentId, laneIndex, laneId, startNode, nextNodeId, buildingId, isTollBooth));
            }
#endif

            // running number of next incoming lanes (number is updated at each segment iteration)
            int totalIncomingLanes = 0;

            // running number of next outgoing lanes (number is updated at each segment iteration)
            int totalOutgoingLanes = 0;

            for (int k = 0; k < 8; ++k) {
                if (!iterateViaGeometry) {
                    Constants.ServiceFactory.NetService.ProcessNode(
                        nextNodeId,
                        (ushort nId, ref NetNode node) => {
                            nextSegmentId = node.GetSegment(k);
                            return true;
                        });

                    if (nextSegmentId == 0) {
                        continue;
                    }
                }

                int outgoingVehicleLanes = 0;
                int incomingVehicleLanes = 0;
                bool isNextStartNodeOfNextSegment = false;
                bool nextSegIsInverted = false;
                NetInfo nextSegmentInfo = null;
                uint nextFirstLaneId = 0;

                Constants.ServiceFactory.NetService.ProcessSegment(
                    nextSegmentId,
                    (ushort nextSegId, ref NetSegment segment) => {
                        isNextStartNodeOfNextSegment = segment.m_startNode == nextNodeId;
                        /*segment.UpdateLanes(nextSegmentId, true);
                        if (isNextStartNodeOfNextSegment) {
                                segment.UpdateStartSegments(nextSegmentId);
                        } else {
                                segment.UpdateEndSegments(nextSegmentId);
                        }*/
                        nextSegmentInfo = segment.Info;
                        nextSegIsInverted =
                            (segment.m_flags & NetSegment.Flags.Invert) !=
                            NetSegment.Flags.None;
                        nextFirstLaneId = segment.m_lanes;
                        return true;
                    });

                bool nextIsHighway = Constants.ManagerFactory.ExtSegmentManager.CalculateIsHighway(nextSegmentId);
                bool nextHasBusLane = Constants.ManagerFactory.ExtSegmentManager.CalculateHasBusLane(nextSegmentId);

#if DEBUGROUTING
                if (extendedLogRouting) {
                    Log._Debug(string.Format(
                        "RoutingManager.RecalculateLaneEndRoutingData({0}, {1}, {2}, {3}): Exploring " +
                        "nextSegmentId={4}",
                        segmentId, laneIndex, laneId, startNode, nextSegmentId));
                    Log._Debug(string.Format(
                        "RoutingManager.RecalculateLaneEndRoutingData({0}, {1}, {2}, {3}): " +
                        "isNextStartNodeOfNextSegment={4} nextSegIsInverted={5} nextFirstLaneId={6} " +
                        "nextIsHighway={7} nextHasBusLane={8} totalOutgoingLanes={9} totalIncomingLanes={10}",
                        segmentId, laneIndex, laneId, startNode, isNextStartNodeOfNextSegment, nextSegIsInverted,
                        nextFirstLaneId, nextIsHighway, nextHasBusLane, totalOutgoingLanes, totalIncomingLanes));
                }
#endif

                // determine next segment direction by evaluating the geometry information
                ArrowDirection nextIncomingDir = segEndMan.GetDirection(ref prevEnd, nextSegmentId);
                bool isNextSegmentValid = nextIncomingDir != ArrowDirection.None;

#if DEBUGROUTING
                if (extendedLogRouting) {
                    Log._Debug(string.Format(
                        "RoutingManager.RecalculateLaneEndRoutingData({0}, {1}, {2}, {3}): " +
                        "prevSegment={4}. Exploring nextSegment={5} -- nextFirstLaneId={6} " +
                        "-- nextIncomingDir={7} valid={8}",
                        segmentId, laneIndex, laneId, startNode, segmentId, nextSegmentId, nextFirstLaneId,
                        nextIncomingDir, isNextSegmentValid));
                }
#endif

                NetInfo.Direction nextDir = isNextStartNodeOfNextSegment
                                                ? NetInfo.Direction.Backward
                                                : NetInfo.Direction.Forward;
                NetInfo.Direction nextDir2 =
                    !nextSegIsInverted ? nextDir : NetInfo.InvertDirection(nextDir);

                LaneTransitionData[] nextRelaxedTransitionDatas = null;
                byte numNextRelaxedTransitionDatas = 0;
                LaneTransitionData[] nextCompatibleTransitionDatas = null;
                int[] nextCompatibleOuterSimilarIndices = null;
                byte numNextCompatibleTransitionDatas = 0;
                LaneTransitionData[] nextLaneConnectionTransitionDatas = null;
                byte numNextLaneConnectionTransitionDatas = 0;
                LaneTransitionData[] nextForcedTransitionDatas = null;
                byte numNextForcedTransitionDatas = 0;
                int[] nextCompatibleTransitionDataIndices = null;
                byte numNextCompatibleTransitionDataIndices = 0;
                int[] compatibleLaneIndexToLaneConnectionIndex = null;

                if (isNextSegmentValid) {
                    nextRelaxedTransitionDatas = new LaneTransitionData[MAX_NUM_TRANSITIONS];
                    nextCompatibleTransitionDatas = new LaneTransitionData[MAX_NUM_TRANSITIONS];
                    nextLaneConnectionTransitionDatas = new LaneTransitionData[MAX_NUM_TRANSITIONS];
                    nextForcedTransitionDatas = new LaneTransitionData[MAX_NUM_TRANSITIONS];
                    nextCompatibleOuterSimilarIndices = new int[MAX_NUM_TRANSITIONS];
                    nextCompatibleTransitionDataIndices = new int[MAX_NUM_TRANSITIONS];
                    compatibleLaneIndexToLaneConnectionIndex = new int[MAX_NUM_TRANSITIONS];
                }

                uint nextLaneId = nextFirstLaneId;
                byte nextLaneIndex = 0;

                //ushort compatibleLaneIndicesMask = 0;
                while (nextLaneIndex < nextSegmentInfo.m_lanes.Length && nextLaneId != 0u) {
                    // determine valid lanes based on lane arrows
                    NetInfo.Lane nextLaneInfo = nextSegmentInfo.m_lanes[nextLaneIndex];

#if DEBUGROUTING
                    if (extendedLogRouting) {
                        Log._Debug(string.Format(
                            "RoutingManager.RecalculateLaneEndRoutingData({0}, {1}, {2}, {3}): " +
                            "prevSegment={4}. Exploring nextSegment={5}, lane {6}, idx {7}",
                            segmentId, laneIndex, laneId, startNode, segmentId, nextSegmentId,
                            nextLaneId, nextLaneIndex));
                    }
#endif

                    // next is compatible lane
                    if (nextLaneInfo.CheckType(ROUTED_LANE_TYPES, ROUTED_VEHICLE_TYPES) &&
                        (prevLaneInfo.m_vehicleType & nextLaneInfo.m_vehicleType) != VehicleInfo.VehicleType.None
                        /*(nextLaneInfo.m_vehicleType & prevLaneInfo.m_vehicleType) != VehicleInfo.VehicleType.None &&
                        (nextLaneInfo.m_laneType & prevLaneInfo.m_laneType) != NetInfo.LaneType.None*/)
                    {
#if DEBUGROUTING
                        if (extendedLogRouting) {
                            Log._Debug(
                                $"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, " +
                                $"{laneIndex}, {laneId}, {startNode}): vehicle type check passed for " +
                                $"nextLaneId={nextLaneId}, idx={nextLaneIndex}");
                        }
#endif
                        // next is incoming lane
                        if ((nextLaneInfo.m_finalDirection & nextDir2) != NetInfo.Direction.None) {
#if DEBUGROUTING
                            if (extendedLogRouting) {
                                Log._Debug(
                                    $"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, " +
                                    $"{laneIndex}, {laneId}, {startNode}): lane direction check passed " +
                                    $"for nextLaneId={nextLaneId}, idx={nextLaneIndex}");
                            }
#endif
                            ++incomingVehicleLanes;

#if DEBUGROUTING
                            if (extendedLogRouting) {
                                Log._Debug(string.Format(
                                    "RoutingManager.RecalculateLaneEndRoutingData({0}, {1}, {2}, {3}): " +
                                    "increasing number of incoming lanes at nextLaneId={4}, idx={5}: " +
                                    "isNextValid={6}, nextLaneInfo.m_finalDirection={7}, nextDir2={8}: " +
                                    "incomingVehicleLanes={9}, outgoingVehicleLanes={10} ",
                                    segmentId, laneIndex, laneId, startNode, nextLaneId, nextLaneIndex,
                                    isNextSegmentValid, nextLaneInfo.m_finalDirection, nextDir2,
                                    incomingVehicleLanes, outgoingVehicleLanes));
                            }
#endif

                            if (isNextSegmentValid) {
                                // calculate current similar lane index starting from outer lane
                                int nextOuterSimilarLaneIndex = CalcOuterSimilarLaneIndex(nextSegmentId, nextLaneIndex);

                                //int nextInnerSimilarLaneIndex = CalcInnerSimilarLaneIndex(nextSegmentId, nextLaneIndex);
                                bool isCompatibleLane = false;
                                LaneEndTransitionType transitionType = LaneEndTransitionType.Invalid;

                                // check for lane connections
                                bool nextHasOutgoingConnections =
                                    LaneConnectionManager.Instance.HasConnections(
                                        nextLaneId,
                                        isNextStartNodeOfNextSegment);
                                bool nextIsConnectedWithPrev = true;
                                if (nextHasOutgoingConnections) {
                                    nextIsConnectedWithPrev =
                                        LaneConnectionManager.Instance.AreLanesConnected(
                                            prevLaneId,
                                            nextLaneId,
                                            startNode);
                                }

#if DEBUGROUTING
                                if (extendedLogRouting) {
                                    Log._Debug(string.Format(
                                        "RoutingManager.RecalculateLaneEndRoutingData({0}, {1}, {2}, {3}): " +
                                        "checking lane connections of nextLaneId={4}, idx={5}: " +
                                        "isNextStartNodeOfNextSegment={6}, nextSegmentId={7}, " +
                                        "nextHasOutgoingConnections={8}, nextIsConnectedWithPrev={9}",
                                        segmentId, laneIndex, laneId, startNode, nextLaneId, nextLaneIndex,
                                        isNextStartNodeOfNextSegment, nextSegmentId, nextHasOutgoingConnections,
                                        nextIsConnectedWithPrev));
                                }
#endif


#if DEBUGROUTING
                                if (extendedLogRouting) {
                                    Log._Debug(string.Format(
                                        "RoutingManager.RecalculateLaneEndRoutingData({0}, {1}, {2}, {3}): " +
                                        "connection information for nextLaneId={4}, idx={5}: " +
                                        "nextOuterSimilarLaneIndex={6}, nextHasOutgoingConnections={7}, " +
                                        "nextIsConnectedWithPrev={8}",
                                        segmentId, laneIndex, laneId, startNode, nextLaneId, nextLaneIndex,
                                        nextOuterSimilarLaneIndex, nextHasOutgoingConnections,
                                        nextIsConnectedWithPrev));
                                }
#endif

                                int currentLaneConnectionTransIndex = -1;

                                if (nextHasOutgoingConnections) {
                                    // check for lane connections
                                    if (nextIsConnectedWithPrev) {
                                        // lane is connected with previous lane
                                        if (numNextLaneConnectionTransitionDatas < MAX_NUM_TRANSITIONS) {
                                            currentLaneConnectionTransIndex =
                                                numNextLaneConnectionTransitionDatas;

                                            nextLaneConnectionTransitionDatas
                                                [numNextLaneConnectionTransitionDatas++].Set(
                                                nextLaneId,
                                                nextLaneIndex,
                                                LaneEndTransitionType.LaneConnection,
                                                nextSegmentId,
                                                isNextStartNodeOfNextSegment);
                                        } else {
                                            Log.Warning(
                                                $"nextTransitionDatas overflow @ source lane {prevLaneId}, " +
                                                $"idx {prevLaneIndex} @ seg. {prevSegmentId}");
                                        }
#if DEBUGROUTING
                                        if (extendedLogRouting) {
                                            Log._Debug(string.Format(
                                                "RoutingManager.RecalculateLaneEndRoutingData({0}, {1}, " +
                                                "{2}, {3}): nextLaneId={4}, idx={5} has outgoing connections " +
                                                "and is connected with previous lane. adding as lane connection lane.",
                                                segmentId, laneIndex, laneId, startNode, nextLaneId, nextLaneIndex));
                                        }
#endif
                                    } else {
#if DEBUGROUTING
                                        if (extendedLogRouting) {
                                            Log._Debug(string.Format(
                                                "RoutingManager.RecalculateLaneEndRoutingData({0}, {1}, " +
                                                "{2}, {3}): nextLaneId={4}, idx={5} has outgoing connections " +
                                                "but is NOT connected with previous lane",
                                                segmentId, laneIndex, laneId, startNode, nextLaneId, nextLaneIndex));
                                        }
#endif
                                    }
                                }

                                if (isTollBooth) {
#if DEBUGROUTING
                                    if (extendedLogRouting)
                                        Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): nextNodeId={nextNodeId}, buildingId={buildingId} is a toll booth. Preventing lane changes.");
#endif
                                    if (nextOuterSimilarLaneIndex == prevOuterSimilarLaneIndex) {
#if DEBUGROUTING
                                        if (extendedLogRouting)
                                            Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): nextLaneId={nextLaneId}, idx={nextLaneIndex} is associated with a toll booth (buildingId={buildingId}). adding as Default.");
#endif
                                        isCompatibleLane = true;
                                        transitionType = LaneEndTransitionType.Default;
                                    }
                                } else if (!nextIsJunction) {
#if DEBUGROUTING
                                    if (extendedLogRouting)
                                        Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): nextLaneId={nextLaneId}, idx={nextLaneIndex} is not a junction. adding as Default.");
#endif
                                    isCompatibleLane = true;
                                    transitionType = LaneEndTransitionType.Default;
                                } else if (nextLaneInfo.CheckType(ROUTED_LANE_TYPES, ARROW_VEHICLE_TYPES)) {
                                    // check for lane arrows
                                    LaneArrows nextLaneArrows = LaneArrowManager.Instance.GetFinalLaneArrows(nextLaneId);
                                    bool hasLeftArrow = (nextLaneArrows & LaneArrows.Left) != LaneArrows.None;
                                    bool hasRightArrow = (nextLaneArrows & LaneArrows.Right) != LaneArrows.None;
                                    bool hasForwardArrow = (nextLaneArrows & LaneArrows.Forward) != LaneArrows.None || (nextLaneArrows & LaneArrows.LeftForwardRight) == LaneArrows.None;

#if DEBUGROUTING
                                    if (extendedLogRouting)
                                        Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): start lane arrow check for nextLaneId={nextLaneId}, idx={nextLaneIndex}: hasLeftArrow={hasLeftArrow}, hasForwardArrow={hasForwardArrow}, hasRightArrow={hasRightArrow}");
#endif

                                    if (applyHighwayRules || // highway rules enabled
                                        (nextIncomingDir == ArrowDirection.Right && hasLeftArrow) || // valid incoming right
                                        (nextIncomingDir == ArrowDirection.Left && hasRightArrow) || // valid incoming left
                                        (nextIncomingDir == ArrowDirection.Forward && hasForwardArrow) || // valid incoming straight
                                        (nextIncomingDir == ArrowDirection.Turn && (!nextIsRealJunction || nextIsEndOrOneWayOut || ((leftHandDrive && hasRightArrow) || (!leftHandDrive && hasLeftArrow))))) { // valid turning lane
#if DEBUGROUTING
                                        if (extendedLogRouting)
                                            Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): lane arrow check passed for nextLaneId={nextLaneId}, idx={nextLaneIndex}. adding as default lane.");
#endif
                                        isCompatibleLane = true;
                                        transitionType = LaneEndTransitionType.Default;
                                    } else if (nextIsConnectedWithPrev) {
#if DEBUGROUTING
                                        if (extendedLogRouting)
                                            Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): lane arrow check FAILED for nextLaneId={nextLaneId}, idx={nextLaneIndex}. adding as relaxed lane.");
#endif

                                        // lane can be used by all vehicles that may disregard lane arrows
                                        transitionType = LaneEndTransitionType.Relaxed;
                                        if (numNextRelaxedTransitionDatas < MAX_NUM_TRANSITIONS) {
                                            nextRelaxedTransitionDatas[numNextRelaxedTransitionDatas++].Set(nextLaneId, nextLaneIndex, transitionType, nextSegmentId, isNextStartNodeOfNextSegment, GlobalConfig.Instance.PathFinding.IncompatibleLaneDistance);
                                        } else {
                                            Log.Warning($"nextTransitionDatas overflow @ source lane {prevLaneId}, idx {prevLaneIndex} @ seg. {prevSegmentId}");
                                        }
                                    }
                                } else if (!nextHasOutgoingConnections) {
#if DEBUGROUTING
                                    if (extendedLogRouting)
                                        Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): nextLaneId={nextLaneId}, idx={nextLaneIndex} is used by vehicles that do not follow lane arrows. adding as default.");
#endif

                                    // routed vehicle that does not follow lane arrows (trains, trams, metros, monorails)
                                    transitionType = LaneEndTransitionType.Default;

                                    if (numNextForcedTransitionDatas < MAX_NUM_TRANSITIONS) {
                                        nextForcedTransitionDatas[numNextForcedTransitionDatas].Set(nextLaneId, nextLaneIndex, transitionType, nextSegmentId, isNextStartNodeOfNextSegment);

                                        if (!nextIsRealJunction) {
                                            // simple forced lane transition: set lane distance
                                            nextForcedTransitionDatas[numNextForcedTransitionDatas].distance = (byte)Math.Abs(prevOuterSimilarLaneIndex - nextOuterSimilarLaneIndex);
                                        }

                                        ++numNextForcedTransitionDatas;
                                    } else {
                                        Log.Warning($"nextForcedTransitionDatas overflow @ source lane {prevLaneId}, idx {prevLaneIndex} @ seg. {prevSegmentId}");
                                    }
                                }

                                if (isCompatibleLane) {
#if DEBUGROUTING
                                    if (extendedLogRouting)
                                        Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): adding nextLaneId={nextLaneId}, idx={nextLaneIndex} as compatible lane now.");
#endif

                                    if (numNextCompatibleTransitionDatas < MAX_NUM_TRANSITIONS) {
                                        nextCompatibleOuterSimilarIndices[numNextCompatibleTransitionDatas] = nextOuterSimilarLaneIndex;
                                        compatibleLaneIndexToLaneConnectionIndex[numNextCompatibleTransitionDatas] = currentLaneConnectionTransIndex;
                                        //compatibleLaneIndicesMask |= POW2MASKS[numNextCompatibleTransitionDatas];
                                        nextCompatibleTransitionDatas[numNextCompatibleTransitionDatas++].Set(nextLaneId, nextLaneIndex, transitionType, nextSegmentId, isNextStartNodeOfNextSegment);
                                    } else {
                                        Log.Warning($"nextCompatibleTransitionDatas overflow @ source lane {prevLaneId}, idx {prevLaneIndex} @ seg. {prevSegmentId}");
                                    }
                                } else {
#if DEBUGROUTING
                                    if (extendedLogRouting)
                                        Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): nextLaneId={nextLaneId}, idx={nextLaneIndex} is NOT compatible.");
#endif
                                }
                            }
                        } else {
#if DEBUGROUTING
                            if (extendedLogRouting)
                                Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): lane direction check NOT passed for nextLaneId={nextLaneId}, idx={nextLaneIndex}: isNextValid={isNextSegmentValid}, nextLaneInfo.m_finalDirection={nextLaneInfo.m_finalDirection}, nextDir2={nextDir2}");
#endif
                            if ((nextLaneInfo.m_finalDirection & NetInfo.InvertDirection(nextDir2)) != NetInfo.Direction.None) {
                                ++outgoingVehicleLanes;
#if DEBUGROUTING
                                if (extendedLogRouting)
                                    Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): increasing number of outgoing lanes at nextLaneId={nextLaneId}, idx={nextLaneIndex}: isNextValid={isNextSegmentValid}, nextLaneInfo.m_finalDirection={nextLaneInfo.m_finalDirection}, nextDir2={nextDir2}: incomingVehicleLanes={incomingVehicleLanes}, outgoingVehicleLanes={outgoingVehicleLanes}");
#endif
                            }
                        }
                    } else {
#if DEBUGROUTING
                        if (extendedLogRouting)
                            Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): vehicle type check NOT passed for nextLaneId={nextLaneId}, idx={nextLaneIndex}: prevLaneInfo.m_vehicleType={prevLaneInfo.m_vehicleType}, nextLaneInfo.m_vehicleType={nextLaneInfo.m_vehicleType}, prevLaneInfo.m_laneType={prevLaneInfo.m_laneType}, nextLaneInfo.m_laneType={nextLaneInfo.m_laneType}");
#endif
                    }

                    Constants.ServiceFactory.NetService.ProcessLane(nextLaneId, delegate (uint lId, ref NetLane lane) {
                        nextLaneId = lane.m_nextLane;
                        return true;
                    });
                    ++nextLaneIndex;
                } // foreach lane


#if DEBUGROUTING
                if (extendedLogRouting)
                    Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): isNextValid={isNextSegmentValid} Compatible lanes: " + nextCompatibleTransitionDatas?.ArrayToString());
#endif
                if (isNextSegmentValid) {
                    bool laneChangesAllowed = Options.junctionRestrictionsEnabled && JunctionRestrictionsManager.Instance.IsLaneChangingAllowedWhenGoingStraight(nextSegmentId, isNextStartNodeOfNextSegment);
                    int nextCompatibleLaneCount = numNextCompatibleTransitionDatas;
                    if (nextCompatibleLaneCount > 0) {
                        // we found compatible lanes

                        int[] tmp = new int[nextCompatibleLaneCount];
                        Array.Copy(nextCompatibleOuterSimilarIndices, tmp, nextCompatibleLaneCount);
                        nextCompatibleOuterSimilarIndices = tmp;

                        int[] compatibleLaneIndicesSortedByOuterSimilarIndex = nextCompatibleOuterSimilarIndices.Select((x, i) => new KeyValuePair<int, int>(x, i)).OrderBy(p => p.Key).Select(p => p.Value).ToArray();

                        // enable highway rules only at junctions or at simple lane merging/splitting points
                        int laneDiff = nextCompatibleLaneCount - prevSimilarLaneCount;
                        bool applyHighwayRulesAtSegment = applyHighwayRules && (applyHighwayRulesAtJunction || Math.Abs(laneDiff) == 1);

#if DEBUGROUTING
                        if (extendedLogRouting)
                            Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): found compatible lanes! compatibleLaneIndicesSortedByOuterSimilarIndex={compatibleLaneIndicesSortedByOuterSimilarIndex.ArrayToString()}, laneDiff={laneDiff}, applyHighwayRulesAtSegment={applyHighwayRulesAtSegment}");
#endif

                        if (applyHighwayRulesAtJunction) {
                            // we reached a highway junction where more than two segments are connected to each other
#if DEBUGROUTING
                            if (extendedLogRouting)
                                Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): applying highway rules at junction");
#endif

                            // number of lanes that were processed in earlier segment iterations (either all incoming or all outgoing)
                            int numLanesSeen = Math.Max(totalIncomingLanes, totalOutgoingLanes);

                            int minNextInnerSimilarIndex = -1;
                            int maxNextInnerSimilarIndex = -1;
                            int refNextInnerSimilarIndex = -1; // this lane will be referred as the "stay" lane with zero distance

#if DEBUGHWJUNCTIONROUTING
                            if (extendedLogRouting) {
                                Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): applying highway rules at junction");
                                Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): totalIncomingLanes={totalIncomingLanes}, totalOutgoingLanes={totalOutgoingLanes}, numLanesSeen={numLanesSeen} laneChangesAllowed={laneChangesAllowed}");
                                Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): prevInnerSimilarLaneIndex={prevInnerSimilarLaneIndex}, prevSimilarLaneCount={prevSimilarLaneCount}, nextCompatibleLaneCount={nextCompatibleLaneCount}");
                            }
#endif

                            if (nextIsSplitJunction) {
                                // lane splitting at junction
                                minNextInnerSimilarIndex = prevInnerSimilarLaneIndex + numLanesSeen;

                                if (minNextInnerSimilarIndex >= nextCompatibleLaneCount) {
                                    // there have already been explored more outgoing lanes than incoming lanes on the previous segment. Also allow vehicles to go to the current segment.
                                    minNextInnerSimilarIndex = maxNextInnerSimilarIndex = refNextInnerSimilarIndex = nextCompatibleLaneCount - 1;
                                } else {
                                    maxNextInnerSimilarIndex = refNextInnerSimilarIndex = minNextInnerSimilarIndex;
                                    if (laneChangesAllowed) {
                                        // allow lane changes at highway junctions
                                        if (minNextInnerSimilarIndex > 0 && prevInnerSimilarLaneIndex > 0) {
                                            --minNextInnerSimilarIndex;
                                        }
                                    }
                                }

#if DEBUGHWJUNCTIONROUTING
                                if (extendedLogRouting) {
                                    Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): highway rules at junction: lane splitting junction. minNextInnerSimilarIndex={minNextInnerSimilarIndex}, maxNextInnerSimilarIndex={maxNextInnerSimilarIndex}");
                                }
#endif
                            } else {
                                // lane merging at junction
                                minNextInnerSimilarIndex = prevInnerSimilarLaneIndex - numLanesSeen;

                                if (minNextInnerSimilarIndex < 0) {
                                    if (prevInnerSimilarLaneIndex == prevSimilarLaneCount - 1) {
                                        // there have already been explored more incoming lanes than outgoing lanes on the previous segment. Allow the current segment to also join the big merging party. What a fun!
                                        minNextInnerSimilarIndex = 0;
                                        maxNextInnerSimilarIndex = nextCompatibleLaneCount - 1;
                                    } else {
                                        // lanes do not connect (min/max = -1)
                                    }
                                } else {
                                    // allow lane changes at highway junctions
                                    refNextInnerSimilarIndex = minNextInnerSimilarIndex;
                                    if (laneChangesAllowed) {
                                        maxNextInnerSimilarIndex = Math.Min(nextCompatibleLaneCount - 1, minNextInnerSimilarIndex + 1);
                                        if (minNextInnerSimilarIndex > 0) {
                                            --minNextInnerSimilarIndex;
                                        }
                                    } else {
                                        maxNextInnerSimilarIndex = minNextInnerSimilarIndex;
                                    }

                                    if (totalIncomingLanes > 0 && prevInnerSimilarLaneIndex == prevSimilarLaneCount - 1 && maxNextInnerSimilarIndex < nextCompatibleLaneCount - 1) {
                                        // we reached the outermost lane on the previous segment but there are still lanes to go on the next segment: allow merging
                                        maxNextInnerSimilarIndex = nextCompatibleLaneCount - 1;
                                    }
                                }

#if DEBUGHWJUNCTIONROUTING
                                if (extendedLogRouting) {
                                    Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): highway rules at junction: lane merging/unknown junction. minNextInnerSimilarIndex={minNextInnerSimilarIndex}, maxNextInnerSimilarIndex={maxNextInnerSimilarIndex}");
                                }
#endif
                            }

                            if (minNextInnerSimilarIndex >= 0) {
#if DEBUGHWJUNCTIONROUTING
                                if (extendedLogRouting) {
                                    Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): minNextInnerSimilarIndex >= 0. nextCompatibleTransitionDatas={nextCompatibleTransitionDatas.ArrayToString()}");
                                }
#endif

                                // explore lanes
                                for (int nextInnerSimilarIndex = minNextInnerSimilarIndex; nextInnerSimilarIndex <= maxNextInnerSimilarIndex; ++nextInnerSimilarIndex) {
                                    int nextTransitionIndex = FindLaneByInnerIndex(nextCompatibleTransitionDatas, numNextCompatibleTransitionDatas, nextSegmentId, nextInnerSimilarIndex);

#if DEBUGHWJUNCTIONROUTING
                                    if (extendedLogRouting) {
                                        Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): highway junction iteration: nextInnerSimilarIndex={nextInnerSimilarIndex}, nextTransitionIndex={nextTransitionIndex}");
                                    }
#endif

                                    if (nextTransitionIndex < 0) {
                                        continue;
                                    }

                                    // calculate lane distance
                                    byte compatibleLaneDist = 0;
                                    if (refNextInnerSimilarIndex >= 0) {
                                        compatibleLaneDist = (byte)Math.Abs(refNextInnerSimilarIndex - nextInnerSimilarIndex);
                                    }

                                    // skip lanes having lane connections
                                    if (LaneConnectionManager.Instance.HasConnections(nextCompatibleTransitionDatas[nextTransitionIndex].laneId, isNextStartNodeOfNextSegment)) {
                                        int laneConnectionTransIndex = compatibleLaneIndexToLaneConnectionIndex[nextTransitionIndex];
                                        if (laneConnectionTransIndex >= 0) {
                                            nextLaneConnectionTransitionDatas[laneConnectionTransIndex].distance = compatibleLaneDist;
                                        }
#if DEBUGROUTING
                                        if (extendedLogRouting)
                                            Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): Next lane ({nextCompatibleTransitionDatas[nextTransitionIndex].laneId}) has outgoing lane connections. Skip for now but set compatibleLaneDist={compatibleLaneDist} if laneConnectionTransIndex={laneConnectionTransIndex} >= 0.");
#endif
                                        continue; // disregard lane since it has outgoing connections
                                    }

                                    nextCompatibleTransitionDatas[nextTransitionIndex].distance = compatibleLaneDist;
#if DEBUGHWJUNCTIONROUTING
                                    if (extendedLogRouting) {
                                        Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): highway junction iteration: compatibleLaneDist={compatibleLaneDist}");
                                    }
#endif

                                    UpdateHighwayLaneArrows(nextCompatibleTransitionDatas[nextTransitionIndex].laneId, isNextStartNodeOfNextSegment, nextIncomingDir);

                                    if (numNextCompatibleTransitionDataIndices < MAX_NUM_TRANSITIONS) {
                                        nextCompatibleTransitionDataIndices[numNextCompatibleTransitionDataIndices++] = nextTransitionIndex;
                                    } else {
                                        Log.Warning($"nextCompatibleTransitionDataIndices overflow @ source lane {prevLaneId}, idx {prevLaneIndex} @ seg. {prevSegmentId}");
                                    }
                                }

#if DEBUGHWJUNCTIONROUTING
                                if (extendedLogRouting) {
                                    Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): highway junction iterations finished: nextCompatibleTransitionDataIndices={nextCompatibleTransitionDataIndices.ArrayToString()}");
                                }
#endif
                            }
                        } else {
                            /*
                             * This is
                             *    1. a highway lane splitting/merging point,
                             *    2. a city or highway lane continuation point (simple transition with equal number of lanes or flagged city transition), or
                             *    3. a city junction
                             *  with multiple or a single target lane: Perform lane matching
                             */

#if DEBUGROUTING
                            if (extendedLogRouting)
                                Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): regular node");
#endif

                            // min/max compatible outer similar lane indices
                            int minNextCompatibleOuterSimilarIndex = -1;
                            int maxNextCompatibleOuterSimilarIndex = -1;
                            if (nextIncomingDir == ArrowDirection.Turn) {
                                minNextCompatibleOuterSimilarIndex = 0;
                                maxNextCompatibleOuterSimilarIndex = nextCompatibleLaneCount - 1;
#if DEBUGROUTING
                                if (extendedLogRouting)
                                    Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): u-turn: minNextCompatibleOuterSimilarIndex={minNextCompatibleOuterSimilarIndex}, maxNextCompatibleOuterSimilarIndex={maxNextCompatibleOuterSimilarIndex}");
#endif
                            } else if (nextIsRealJunction) {
#if DEBUGROUTING
                                if (extendedLogRouting)
                                    Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): next is real junction");
#endif

                                // at junctions: try to match distinct lanes
                                if (nextCompatibleLaneCount > prevSimilarLaneCount && prevOuterSimilarLaneIndex == prevSimilarLaneCount - 1) {
                                    // merge inner lanes
                                    minNextCompatibleOuterSimilarIndex = prevOuterSimilarLaneIndex;
                                    maxNextCompatibleOuterSimilarIndex = nextCompatibleLaneCount - 1;
#if DEBUGROUTING
                                    if (extendedLogRouting)
                                        Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): merge inner lanes: minNextCompatibleOuterSimilarIndex={minNextCompatibleOuterSimilarIndex}, maxNextCompatibleOuterSimilarIndex={maxNextCompatibleOuterSimilarIndex}");
#endif
                                } else if (nextCompatibleLaneCount < prevSimilarLaneCount && prevSimilarLaneCount % nextCompatibleLaneCount == 0) {
                                    // symmetric split
                                    int splitFactor = prevSimilarLaneCount / nextCompatibleLaneCount;
                                    minNextCompatibleOuterSimilarIndex = maxNextCompatibleOuterSimilarIndex = prevOuterSimilarLaneIndex / splitFactor;
#if DEBUGROUTING
                                    if (extendedLogRouting)
                                        Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): symmetric split: minNextCompatibleOuterSimilarIndex={minNextCompatibleOuterSimilarIndex}, maxNextCompatibleOuterSimilarIndex={maxNextCompatibleOuterSimilarIndex}");
#endif
                                } else {
                                    // 1-to-n (split inner lane) or 1-to-1 (direct lane matching)
                                    minNextCompatibleOuterSimilarIndex = prevOuterSimilarLaneIndex;
                                    maxNextCompatibleOuterSimilarIndex = prevOuterSimilarLaneIndex;
#if DEBUGROUTING
                                    if (extendedLogRouting)
                                        Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): 1-to-n (split inner lane) or 1-to-1 (direct lane matching): minNextCompatibleOuterSimilarIndex={minNextCompatibleOuterSimilarIndex}, maxNextCompatibleOuterSimilarIndex={maxNextCompatibleOuterSimilarIndex}");
#endif
                                }

                                bool straightLaneChangesAllowed = nextIncomingDir == ArrowDirection.Forward && laneChangesAllowed;

#if DEBUGROUTING
                                if (extendedLogRouting)
                                    Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): laneChangesAllowed={laneChangesAllowed} straightLaneChangesAllowed={straightLaneChangesAllowed}");
#endif

                                if (!straightLaneChangesAllowed) {
                                    if (nextHasBusLane && !prevHasBusLane) {
                                        // allow vehicles on the bus lane AND on the next lane to merge on this lane
                                        maxNextCompatibleOuterSimilarIndex = Math.Min(nextCompatibleLaneCount - 1, maxNextCompatibleOuterSimilarIndex + 1);
#if DEBUGROUTING
                                        if (extendedLogRouting)
                                            Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): allow vehicles on the bus lane AND on the next lane to merge on this lane: minNextCompatibleOuterSimilarIndex={minNextCompatibleOuterSimilarIndex}, maxNextCompatibleOuterSimilarIndex={maxNextCompatibleOuterSimilarIndex}");
#endif
                                    } else if (!nextHasBusLane && prevHasBusLane) {
                                        // allow vehicles to enter the bus lane
                                        minNextCompatibleOuterSimilarIndex = Math.Max(0, minNextCompatibleOuterSimilarIndex - 1);
#if DEBUGROUTING
                                        if (extendedLogRouting)
                                            Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): allow vehicles to enter the bus lane: minNextCompatibleOuterSimilarIndex={minNextCompatibleOuterSimilarIndex}, maxNextCompatibleOuterSimilarIndex={maxNextCompatibleOuterSimilarIndex}");
#endif
                                    }
                                } else {
                                    // vehicles may change lanes when going straight
                                    minNextCompatibleOuterSimilarIndex = minNextCompatibleOuterSimilarIndex - 1;
                                    maxNextCompatibleOuterSimilarIndex = maxNextCompatibleOuterSimilarIndex + 1;
#if DEBUGROUTING
                                    if (extendedLogRouting)
                                        Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): vehicles may change lanes when going straight: minNextCompatibleOuterSimilarIndex={minNextCompatibleOuterSimilarIndex}, maxNextCompatibleOuterSimilarIndex={maxNextCompatibleOuterSimilarIndex}");
#endif
                                }
                            } else if (prevSimilarLaneCount == nextCompatibleLaneCount) {
                                // equal lane count: consider all available lanes
                                minNextCompatibleOuterSimilarIndex = 0;
                                maxNextCompatibleOuterSimilarIndex = nextCompatibleLaneCount - 1;
#if DEBUGROUTING
                                if (extendedLogRouting)
                                    Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): equal lane count: minNextCompatibleOuterSimilarIndex={minNextCompatibleOuterSimilarIndex}, maxNextCompatibleOuterSimilarIndex={maxNextCompatibleOuterSimilarIndex}");
#endif
                            } else {
                                // lane continuation point: lane merging/splitting

#if DEBUGROUTING
                                if (extendedLogRouting)
                                    Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): lane continuation point: lane merging/splitting");
#endif

                                bool sym1 = (prevSimilarLaneCount & 1) == 0; // mod 2 == 0
                                bool sym2 = (nextCompatibleLaneCount & 1) == 0; // mod 2 == 0
#if DEBUGROUTING
                                if (extendedLogRouting)
                                    Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): sym1={sym1}, sym2={sym2}");
#endif
                                if (prevSimilarLaneCount < nextCompatibleLaneCount) {
#if DEBUGROUTING
                                    if (extendedLogRouting)
                                        Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): lane merging (prevSimilarLaneCount={prevSimilarLaneCount} < nextCompatibleLaneCount={nextCompatibleLaneCount})");
#endif

                                    // lane merging
                                    if (sym1 == sym2) {
                                        // merge outer lanes
                                        int a = (nextCompatibleLaneCount - prevSimilarLaneCount) >> 1; // nextCompatibleLaneCount - prevSimilarLaneCount is always > 0
#if DEBUGROUTING
                                        if (extendedLogRouting)
                                            Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): merge outer lanes. a={a}");
#endif
                                        if (prevSimilarLaneCount == 1) {
                                            minNextCompatibleOuterSimilarIndex = 0;
                                            maxNextCompatibleOuterSimilarIndex = nextCompatibleLaneCount - 1; // always >=0
#if DEBUGROUTING
                                            if (extendedLogRouting)
                                                Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): prevSimilarLaneCount == 1: minNextCompatibleOuterSimilarIndex={minNextCompatibleOuterSimilarIndex}, maxNextCompatibleOuterSimilarIndex={maxNextCompatibleOuterSimilarIndex}");
#endif
                                        } else if (prevOuterSimilarLaneIndex == 0) {
                                            minNextCompatibleOuterSimilarIndex = 0;
                                            maxNextCompatibleOuterSimilarIndex = a;
#if DEBUGROUTING
                                            if (extendedLogRouting)
                                                Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): prevOuterSimilarLaneIndex == 0: minNextCompatibleOuterSimilarIndex={minNextCompatibleOuterSimilarIndex}, maxNextCompatibleOuterSimilarIndex={maxNextCompatibleOuterSimilarIndex}");
#endif
                                        } else if (prevOuterSimilarLaneIndex == prevSimilarLaneCount - 1) {
                                            minNextCompatibleOuterSimilarIndex = prevOuterSimilarLaneIndex + a;
                                            maxNextCompatibleOuterSimilarIndex = nextCompatibleLaneCount - 1; // always >=0
#if DEBUGROUTING
                                            if (extendedLogRouting)
                                                Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): prevOuterSimilarLaneIndex == prevSimilarLaneCount - 1: minNextCompatibleOuterSimilarIndex={minNextCompatibleOuterSimilarIndex}, maxNextCompatibleOuterSimilarIndex={maxNextCompatibleOuterSimilarIndex}");
#endif
                                        } else {
                                            minNextCompatibleOuterSimilarIndex = maxNextCompatibleOuterSimilarIndex = prevOuterSimilarLaneIndex + a;
#if DEBUGROUTING
                                            if (extendedLogRouting)
                                                Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): default case: minNextCompatibleOuterSimilarIndex={minNextCompatibleOuterSimilarIndex}, maxNextCompatibleOuterSimilarIndex={maxNextCompatibleOuterSimilarIndex}");
#endif
                                        }
                                    } else {
                                        // criss-cross merge
                                        int a = (nextCompatibleLaneCount - prevSimilarLaneCount - 1) >> 1; // nextCompatibleLaneCount - prevSimilarLaneCount - 1 is always >= 0
                                        int b = (nextCompatibleLaneCount - prevSimilarLaneCount + 1) >> 1; // nextCompatibleLaneCount - prevSimilarLaneCount + 1 is always >= 2
#if DEBUGROUTING
                                        if (extendedLogRouting)
                                            Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): criss-cross merge: a={a}, b={b}");
#endif
                                        if (prevSimilarLaneCount == 1) {
                                            minNextCompatibleOuterSimilarIndex = 0;
                                            maxNextCompatibleOuterSimilarIndex = nextCompatibleLaneCount - 1; // always >=0
#if DEBUGROUTING
                                            if (extendedLogRouting)
                                                Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): prevSimilarLaneCount == 1: minNextCompatibleOuterSimilarIndex={minNextCompatibleOuterSimilarIndex}, maxNextCompatibleOuterSimilarIndex={maxNextCompatibleOuterSimilarIndex}");
#endif
                                        } else if (prevOuterSimilarLaneIndex == 0) {
                                            minNextCompatibleOuterSimilarIndex = 0;
                                            maxNextCompatibleOuterSimilarIndex = b;
#if DEBUGROUTING
                                            if (extendedLogRouting)
                                                Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): prevOuterSimilarLaneIndex == 0: minNextCompatibleOuterSimilarIndex={minNextCompatibleOuterSimilarIndex}, maxNextCompatibleOuterSimilarIndex={maxNextCompatibleOuterSimilarIndex}");
#endif
                                        } else if (prevOuterSimilarLaneIndex == prevSimilarLaneCount - 1) {
                                            minNextCompatibleOuterSimilarIndex = prevOuterSimilarLaneIndex + a;
                                            maxNextCompatibleOuterSimilarIndex = nextCompatibleLaneCount - 1; // always >=0
#if DEBUGROUTING
                                            if (extendedLogRouting)
                                                Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): prevOuterSimilarLaneIndex == prevSimilarLaneCount - 1: minNextCompatibleOuterSimilarIndex={minNextCompatibleOuterSimilarIndex}, maxNextCompatibleOuterSimilarIndex={maxNextCompatibleOuterSimilarIndex}");
#endif
                                        } else {
                                            minNextCompatibleOuterSimilarIndex = prevOuterSimilarLaneIndex + a;
                                            maxNextCompatibleOuterSimilarIndex = prevOuterSimilarLaneIndex + b;
#if DEBUGROUTING
                                            if (extendedLogRouting)
                                                Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): default criss-cross case: minNextCompatibleOuterSimilarIndex={minNextCompatibleOuterSimilarIndex}, maxNextCompatibleOuterSimilarIndex={maxNextCompatibleOuterSimilarIndex}");
#endif
                                        }
                                    }
                                } else {
                                    // at lane splits: distribute traffic evenly (1-to-n, n-to-n)
                                    // prevOuterSimilarIndex is always > nextCompatibleLaneCount
#if DEBUGROUTING
                                    if (extendedLogRouting)
                                        Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): at lane splits: distribute traffic evenly (1-to-n, n-to-n)");
#endif
                                    if (sym1 == sym2) {
                                        // split outer lanes
                                        int a = (prevSimilarLaneCount - nextCompatibleLaneCount) >> 1; // prevSimilarLaneCount - nextCompatibleLaneCount is always > 0
                                        minNextCompatibleOuterSimilarIndex = maxNextCompatibleOuterSimilarIndex = prevOuterSimilarLaneIndex - a; // a is always <= prevSimilarLaneCount
#if DEBUGROUTING
                                        if (extendedLogRouting)
                                            Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): split outer lanes: minNextCompatibleOuterSimilarIndex={minNextCompatibleOuterSimilarIndex}, maxNextCompatibleOuterSimilarIndex={maxNextCompatibleOuterSimilarIndex}");
#endif
                                    } else {
                                        // split outer lanes, criss-cross inner lanes
                                        int a = (prevSimilarLaneCount - nextCompatibleLaneCount - 1) >> 1; // prevSimilarLaneCount - nextCompatibleLaneCount - 1 is always >= 0

                                        minNextCompatibleOuterSimilarIndex = (a - 1 >= prevOuterSimilarLaneIndex) ? 0 : prevOuterSimilarLaneIndex - a - 1;
                                        maxNextCompatibleOuterSimilarIndex = (a >= prevOuterSimilarLaneIndex) ? 0 : prevOuterSimilarLaneIndex - a;
#if DEBUGROUTING
                                        if (extendedLogRouting)
                                            Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): split outer lanes, criss-cross inner lanes: minNextCompatibleOuterSimilarIndex={minNextCompatibleOuterSimilarIndex}, maxNextCompatibleOuterSimilarIndex={maxNextCompatibleOuterSimilarIndex}");
#endif
                                    }
                                }
                            }

#if DEBUGROUTING
                            if (extendedLogRouting)
                                Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): pre-final bounds: minNextCompatibleOuterSimilarIndex={minNextCompatibleOuterSimilarIndex}, maxNextCompatibleOuterSimilarIndex={maxNextCompatibleOuterSimilarIndex}");
#endif

                            minNextCompatibleOuterSimilarIndex = Math.Max(0, Math.Min(minNextCompatibleOuterSimilarIndex, nextCompatibleLaneCount - 1));
                            maxNextCompatibleOuterSimilarIndex = Math.Max(0, Math.Min(maxNextCompatibleOuterSimilarIndex, nextCompatibleLaneCount - 1));

                            if (minNextCompatibleOuterSimilarIndex > maxNextCompatibleOuterSimilarIndex) {
                                minNextCompatibleOuterSimilarIndex = maxNextCompatibleOuterSimilarIndex;
                            }

#if DEBUGROUTING
                            if (extendedLogRouting)
                                Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): final bounds: minNextCompatibleOuterSimilarIndex={minNextCompatibleOuterSimilarIndex}, maxNextCompatibleOuterSimilarIndex={maxNextCompatibleOuterSimilarIndex}");
#endif

                            // find best matching lane(s)
                            for (int nextCompatibleOuterSimilarIndex = minNextCompatibleOuterSimilarIndex; nextCompatibleOuterSimilarIndex <= maxNextCompatibleOuterSimilarIndex; ++nextCompatibleOuterSimilarIndex) {
                                int nextTransitionIndex = FindLaneWithMaxOuterIndex(compatibleLaneIndicesSortedByOuterSimilarIndex, nextCompatibleOuterSimilarIndex);

#if DEBUGROUTING
                                if (extendedLogRouting)
                                    Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): best matching lane iteration -- nextCompatibleOuterSimilarIndex={nextCompatibleOuterSimilarIndex} => nextTransitionIndex={nextTransitionIndex}");
#endif

                                if (nextTransitionIndex < 0) {
                                    continue;
                                }

                                // calculate lane distance
                                byte compatibleLaneDist = 0;
                                if (nextIncomingDir == ArrowDirection.Turn) {
                                    compatibleLaneDist = (byte)GlobalConfig.Instance.PathFinding.UturnLaneDistance;
                                } else if (!nextIsRealJunction && ((!nextIsJunction && !nextIsTransition) || nextCompatibleLaneCount == prevSimilarLaneCount)) {
                                    int relLaneDist = nextCompatibleOuterSimilarIndices[nextTransitionIndex] - prevOuterSimilarLaneIndex; // relative lane distance (positive: change to more outer lane, negative: change to more inner lane)
                                    compatibleLaneDist = (byte)Math.Abs(relLaneDist);
                                }

                                // skip lanes having lane connections
                                if (LaneConnectionManager.Instance.HasConnections(nextCompatibleTransitionDatas[nextTransitionIndex].laneId, isNextStartNodeOfNextSegment)) {
                                    int laneConnectionTransIndex = compatibleLaneIndexToLaneConnectionIndex[nextTransitionIndex];
                                    if (laneConnectionTransIndex >= 0) {
                                        nextLaneConnectionTransitionDatas[laneConnectionTransIndex].distance = compatibleLaneDist;
                                    }
#if DEBUGROUTING
                                    if (extendedLogRouting)
                                        Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): Next lane ({nextCompatibleTransitionDatas[nextTransitionIndex].laneId}) has outgoing lane connections. Skip for now but set compatibleLaneDist={compatibleLaneDist} if laneConnectionTransIndex={laneConnectionTransIndex} >= 0.");
#endif
                                    continue; // disregard lane since it has outgoing connections
                                }

                                if (
                                        nextIncomingDir == ArrowDirection.Turn && // u-turn
                                        !nextIsEndOrOneWayOut && // not a dead end
                                        nextCompatibleOuterSimilarIndex != maxNextCompatibleOuterSimilarIndex // incoming lane is not innermost lane
                                    ) {
                                    // force u-turns to happen on the innermost lane
                                    ++compatibleLaneDist;
                                    nextCompatibleTransitionDatas[nextTransitionIndex].type = LaneEndTransitionType.Relaxed;
#if DEBUGROUTING
                                    if (extendedLogRouting)
                                        Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): Next lane ({nextCompatibleTransitionDatas[nextTransitionIndex].laneId}) is avoided u-turn. Incrementing compatible lane distance to {compatibleLaneDist}");
#endif
                                }

#if DEBUGROUTING
                                if (extendedLogRouting)
                                    Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): -> compatibleLaneDist={compatibleLaneDist}");
#endif

                                nextCompatibleTransitionDatas[nextTransitionIndex].distance = compatibleLaneDist;
                                if (onHighway && !nextIsRealJunction && compatibleLaneDist > 1) {
                                    // under normal circumstances vehicles should not change more than one lane on highways at one time
                                    nextCompatibleTransitionDatas[nextTransitionIndex].type = LaneEndTransitionType.Relaxed;
#if DEBUGROUTING
                                    if (extendedLogRouting)
                                        Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): -> under normal circumstances vehicles should not change more than one lane on highways at one time: setting type to Relaxed");
#endif
                                } else if (applyHighwayRulesAtSegment) {
                                    UpdateHighwayLaneArrows(nextCompatibleTransitionDatas[nextTransitionIndex].laneId, isNextStartNodeOfNextSegment, nextIncomingDir);
                                }

                                if (numNextCompatibleTransitionDataIndices < MAX_NUM_TRANSITIONS) {
                                    nextCompatibleTransitionDataIndices[numNextCompatibleTransitionDataIndices++] = nextTransitionIndex;
                                } else {
                                    Log.Warning($"nextCompatibleTransitionDataIndices overflow @ source lane {prevLaneId}, idx {prevLaneIndex} @ seg. {prevSegmentId}");
                                }
                            } // foreach lane
                        } // highway/city rules if/else
                    } // compatible lanes found

                    // build final array
                    LaneTransitionData[] nextTransitionDatas = new LaneTransitionData[numNextRelaxedTransitionDatas + numNextCompatibleTransitionDataIndices + numNextLaneConnectionTransitionDatas + numNextForcedTransitionDatas];
                    int j = 0;
                    for (int i = 0; i < numNextCompatibleTransitionDataIndices; ++i) {
                        nextTransitionDatas[j++] = nextCompatibleTransitionDatas[nextCompatibleTransitionDataIndices[i]];
                    }

                    for (int i = 0; i < numNextLaneConnectionTransitionDatas; ++i) {
                        nextTransitionDatas[j++] = nextLaneConnectionTransitionDatas[i];
                    }

                    for (int i = 0; i < numNextRelaxedTransitionDatas; ++i) {
                        nextTransitionDatas[j++] = nextRelaxedTransitionDatas[i];
                    }

                    for (int i = 0; i < numNextForcedTransitionDatas; ++i) {
                        nextTransitionDatas[j++] = nextForcedTransitionDatas[i];
                    }

#if DEBUGROUTING
                    if (extendedLogRouting)
                        Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): build array for nextSegment={nextSegmentId}: nextTransitionDatas={nextTransitionDatas.ArrayToString()}");
#endif

                    backwardRouting.AddTransitions(nextTransitionDatas);

#if DEBUGROUTING
                    if (extendedLogRouting)
                        Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): updated incoming/outgoing lanes for next segment iteration: totalIncomingLanes={totalIncomingLanes}, totalOutgoingLanes={totalOutgoingLanes}");
#endif
                } // valid segment

                if (nextSegmentId != prevSegmentId) {
                    totalIncomingLanes += incomingVehicleLanes;
                    totalOutgoingLanes += outgoingVehicleLanes;
                }

                if (iterateViaGeometry) {
                    Constants.ServiceFactory.NetService.ProcessSegment(nextSegmentId, delegate (ushort nextSegId, ref NetSegment segment) {
                        if (Constants.ServiceFactory.SimulationService.LeftHandDrive) {
                            nextSegmentId = segment.GetLeftSegment(nextNodeId);
                        } else {
                            nextSegmentId = segment.GetRightSegment(nextNodeId);
                        }
                        return true;
                    });

                    if (nextSegmentId == prevSegmentId || nextSegmentId == 0) {
                        // we reached the first segment again
                        break;
                    }
                }
            } // foreach segment

            // update backward routing
            LaneEndBackwardRoutings[GetLaneEndRoutingIndex(laneId, startNode)] = backwardRouting;

            // update forward routing
            LaneTransitionData[] newTransitions = backwardRouting.transitions;
            if (newTransitions != null) {
                for (int i = 0; i < newTransitions.Length; ++i) {
                    uint sourceIndex = GetLaneEndRoutingIndex(newTransitions[i].laneId, newTransitions[i].startNode);

                    LaneTransitionData forwardTransition = new LaneTransitionData();
                    forwardTransition.laneId = laneId;
                    forwardTransition.laneIndex = (byte)laneIndex;
                    forwardTransition.type = newTransitions[i].type;
                    forwardTransition.distance = newTransitions[i].distance;
                    forwardTransition.segmentId = segmentId;
                    forwardTransition.startNode = startNode;

                    LaneEndForwardRoutings[sourceIndex].AddTransition(forwardTransition);

#if DEBUGROUTING
                    if (extendedLogRouting)
                        Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): adding transition to forward routing of laneId={laneId}, idx={laneIndex} @ seg. {newTransitions[i].segmentId} @ node {newTransitions[i].startNode} (sourceIndex={sourceIndex}): {forwardTransition.ToString()}\n\nNew forward routing:\n{LaneEndForwardRoutings[sourceIndex].ToString()}");
#endif
                }
            }

#if DEBUGROUTING
            if (logRouting)
                Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData({segmentId}, {laneIndex}, {laneId}, {startNode}): FINISHED calculating routing data for array index {GetLaneEndRoutingIndex(laneId, startNode)}: {backwardRouting}");
#endif
        }

        /// <summary>
        /// remove all backward routings from this lane and forward routings pointing to this lane
        /// </summary>
        /// <param name="laneId"></param>
        /// <param name="startNode"></param>
        protected void ResetLaneRoutings(uint laneId, bool startNode) {
            uint index = GetLaneEndRoutingIndex(laneId, startNode);

            LaneTransitionData[] oldBackwardTransitions = LaneEndBackwardRoutings[index].transitions;
            if (oldBackwardTransitions != null) {
                for (int i = 0; i < oldBackwardTransitions.Length; ++i) {
                    uint sourceIndex = GetLaneEndRoutingIndex(oldBackwardTransitions[i].laneId, oldBackwardTransitions[i].startNode);
                    LaneEndForwardRoutings[sourceIndex].RemoveTransition(laneId);
                }
            }

            LaneEndBackwardRoutings[index].Reset();
        }

        private void UpdateHighwayLaneArrows(uint laneId, bool startNode, ArrowDirection dir) {

            LaneArrows? prevHighwayArrows = Flags.getHighwayLaneArrowFlags(laneId);
            LaneArrows newHighwayArrows = LaneArrows.None;
            if (prevHighwayArrows != null)
                newHighwayArrows = (LaneArrows)prevHighwayArrows;
            if (dir == ArrowDirection.Right)
                newHighwayArrows |= LaneArrows.Left;
            else if (dir == ArrowDirection.Left)
                newHighwayArrows |= LaneArrows.Right;
            else if (dir == ArrowDirection.Forward)
                newHighwayArrows |= LaneArrows.Forward;

#if DEBUGROUTING
            //Log._Debug($"RoutingManager.RecalculateLaneEndRoutingData: highway rules -- next lane {laneId} obeys highway rules. Setting highway lane arrows to {newHighwayArrows}. prevHighwayArrows={prevHighwayArrows}");
#endif

            if (newHighwayArrows != prevHighwayArrows && newHighwayArrows != LaneArrows.None) {
                Flags.setHighwayLaneArrowFlags(laneId, newHighwayArrows, false);
            }
        }

        /*private int GetSegmentNodeIndex(ushort nodeId, ushort segmentId) {
                int i = -1;
                Services.NetService.IterateNodeSegments(nodeId, delegate (ushort segId, ref NetSegment segment) {
                        ++i;
                        if (segId == segmentId) {
                                return false;
                        }
                        return true;
                });
                return i;
        }*/

        public uint GetLaneEndRoutingIndex(uint laneId, bool startNode) {
            return (uint)(laneId + (startNode ? 0u : (uint)NetManager.MAX_LANE_COUNT));
        }

        public int CalcInnerSimilarLaneIndex(ushort segmentId, int laneIndex) {
            int ret = -1;
            Constants.ServiceFactory.NetService.ProcessSegment(segmentId, delegate (ushort segId, ref NetSegment segment) {
                ret = CalcInnerSimilarLaneIndex(segment.Info.m_lanes[laneIndex]);
                return true;
            });

            return ret;
        }

        public int CalcInnerSimilarLaneIndex(NetInfo.Lane laneInfo) {
            // note: m_direction is correct here
            return (byte)(laneInfo.m_direction & NetInfo.Direction.Forward) != 0 ? laneInfo.m_similarLaneIndex : laneInfo.m_similarLaneCount - laneInfo.m_similarLaneIndex - 1;
        }

        public int CalcOuterSimilarLaneIndex(ushort segmentId, int laneIndex) {
            int ret = -1;
            Constants.ServiceFactory.NetService.ProcessSegment(segmentId, delegate (ushort segId, ref NetSegment segment) {
                ret = CalcOuterSimilarLaneIndex(segment.Info.m_lanes[laneIndex]);
                return true;
            });

            return ret;
        }

        public int CalcOuterSimilarLaneIndex(NetInfo.Lane laneInfo) {
            // note: m_direction is correct here
            return (byte)(laneInfo.m_direction & NetInfo.Direction.Forward) != 0 ? laneInfo.m_similarLaneCount - laneInfo.m_similarLaneIndex - 1 : laneInfo.m_similarLaneIndex;
        }

        protected int FindLaneWithMaxOuterIndex(int[] indicesSortedByOuterIndex, int targetOuterLaneIndex) {
            return indicesSortedByOuterIndex[Math.Max(0, Math.Min(targetOuterLaneIndex, indicesSortedByOuterIndex.Length - 1))];
        }

        protected int FindLaneByOuterIndex(LaneTransitionData[] laneTransitions, int num, ushort segmentId, int targetOuterLaneIndex) {
            for (int i = 0; i < num; ++i) {
                int outerIndex = CalcOuterSimilarLaneIndex(segmentId, laneTransitions[i].laneIndex);
                if (outerIndex == targetOuterLaneIndex) {
                    return i;
                }
            }
            return -1;
        }

        protected int FindLaneByInnerIndex(LaneTransitionData[] laneTransitions, int num, ushort segmentId, int targetInnerLaneIndex) {
            for (int i = 0; i < num; ++i) {
                int innerIndex = CalcInnerSimilarLaneIndex(segmentId, laneTransitions[i].laneIndex);
                if (innerIndex == targetInnerLaneIndex) {
                    return i;
                }
            }
            return -1;
        }

        protected bool IsOutgoingLane(ushort segmentId, bool startNode, int laneIndex) {
            return IsIncomingOutgoingLane(segmentId, startNode, laneIndex, false);
        }

        protected bool IsIncomingLane(ushort segmentId, bool startNode, int laneIndex) {
            return IsIncomingOutgoingLane(segmentId, startNode, laneIndex, true);
        }

        protected bool IsIncomingOutgoingLane(ushort segmentId, bool startNode, int laneIndex, bool incoming) {
            bool segIsInverted = false;
            Constants.ServiceFactory.NetService.ProcessSegment(segmentId, delegate (ushort segId, ref NetSegment segment) {
                segIsInverted = (segment.m_flags & NetSegment.Flags.Invert) != NetSegment.Flags.None;
                return true;
            });

            NetInfo.Direction dir = startNode ? NetInfo.Direction.Forward : NetInfo.Direction.Backward;
            dir = incoming ^ segIsInverted ? NetInfo.InvertDirection(dir) : dir;

            NetInfo.Direction finalDir = NetInfo.Direction.None;
            Constants.ServiceFactory.NetService.ProcessSegment(segmentId, delegate (ushort segId, ref NetSegment segment) {
                finalDir = segment.Info.m_lanes[laneIndex].m_finalDirection;
                return true;
            });

            return (finalDir & dir) != NetInfo.Direction.None;
        }

        protected override void HandleInvalidSegment(ref ExtSegment seg) {
#if DEBUG
            bool debug = DebugSwitch.RoutingBasicLog.Get() && (DebugSettings.SegmentId <= 0 || DebugSettings.SegmentId == seg.segmentId);
            if (debug) {
                Log._Debug($"RoutingManager.HandleInvalidSegment({seg.segmentId}) called.");
            }
#endif
            Flags.removeHighwayLaneArrowFlagsAtSegment(seg.segmentId);
            ResetRoutingData(seg.segmentId);
        }

        protected override void HandleValidSegment(ref ExtSegment seg) {
#if DEBUG
            bool debug = DebugSwitch.RoutingBasicLog.Get() && (DebugSettings.SegmentId <= 0 || DebugSettings.SegmentId == seg.segmentId);
            if (debug) {
                Log._Debug($"RoutingManager.HandleValidSegment({seg.segmentId}) called.");
            }
#endif
            ResetRoutingData(seg.segmentId);
            RequestRecalculation(seg.segmentId);
        }

        public override void OnAfterLoadData() {
            base.OnAfterLoadData();

            RecalculateAll();
        }
    }
}