namespace TrafficManager.Manager.Impl {
    using API.Manager;
    using CSUtil.Commons;
    using State.ConfigData;
    using Traffic.Data;

    public class TurnOnRedManager
        : AbstractGeometryObservingManager,
          ITurnOnRedManager
    {
        private TurnOnRedManager() {
            TurnOnRedSegments = new TurnOnRedSegments[2 * NetManager.MAX_SEGMENT_COUNT];
        }

        public static TurnOnRedManager Instance { get; } = new TurnOnRedManager();

        public TurnOnRedSegments[] TurnOnRedSegments { get; }

        public override void OnBeforeLoadData() {
            base.OnBeforeLoadData();

            // JunctionRestrictionsManager requires our data during loading of custom data
            for (uint i = 0; i < NetManager.MAX_SEGMENT_COUNT; ++i) {
                if (!Services.NetService.IsSegmentValid((ushort)i)) {
                    continue;
                }

                HandleValidSegment(ref Constants.ManagerFactory.ExtSegmentManager.ExtSegments[i]);
            }
        }

        public override void OnLevelUnloading() {
            base.OnLevelUnloading();

            for (int i = 0; i < TurnOnRedSegments.Length; ++i) {
                TurnOnRedSegments[i].Reset();
            }
        }

        public int GetIndex(ushort segmentId, bool startNode) {
            return segmentId + (startNode ? 0 : NetManager.MAX_SEGMENT_COUNT);
        }

        protected override void InternalPrintDebugInfo() {
            base.InternalPrintDebugInfo();
            Log._Debug("Turn-on-red segments:");

            for (int i = 0; i < TurnOnRedSegments.Length; ++i) {
                Log._Debug($"Segment end {i}: {TurnOnRedSegments[i]}");
            }
        }

        protected override void HandleValidSegment(ref ExtSegment seg) {
            UpdateSegment(ref seg);
        }

        protected override void HandleInvalidSegment(ref ExtSegment seg) {
            ResetSegment(seg.segmentId);
        }

        protected void UpdateSegment(ref ExtSegment seg) {
#if DEBUG
            if (DebugSwitch.TurnOnRed.Get()) {
                Log._Debug($"TurnOnRedManager.UpdateSegment({seg.segmentId}) called.");
            }
#endif
            ResetSegment(seg.segmentId);

            IExtSegmentEndManager extSegmentEndManager =
                Constants.ManagerFactory.ExtSegmentEndManager;
            ushort startNodeId = Services.NetService.GetSegmentNodeId(seg.segmentId, true);

            if (startNodeId != 0) {
                int index0 = extSegmentEndManager.GetIndex(seg.segmentId, true);
                UpdateSegmentEnd(
                    ref seg,
                    ref extSegmentEndManager.ExtSegmentEnds[index0]);
            }

            ushort endNodeId = Services.NetService.GetSegmentNodeId(seg.segmentId, false);

            if (endNodeId != 0) {
                int index1 = extSegmentEndManager.GetIndex(seg.segmentId, false);
                UpdateSegmentEnd(
                    ref seg,
                    ref extSegmentEndManager.ExtSegmentEnds[index1]);
            }
        }

        protected void UpdateSegmentEnd(ref ExtSegment seg, ref ExtSegmentEnd end) {
#if DEBUG
            bool logTurnOnRed = DebugSwitch.TurnOnRed.Get();
#else
            const bool logTurnOnRed = false;
#endif
            if (logTurnOnRed) {
                Log._Debug($"TurnOnRedManager.UpdateSegmentEnd({end.segmentId}, {end.startNode}) called.");
            }

            IExtSegmentManager segmentManager = Constants.ManagerFactory.ExtSegmentManager;
            IExtSegmentEndManager segmentEndManager = Constants.ManagerFactory.ExtSegmentEndManager;

            ushort segmentId = seg.segmentId;
            ushort nodeId = end.nodeId;
            bool hasOutgoingSegment = false;

            Services.NetService.IterateNodeSegments(
                end.nodeId,
                (ushort otherSegId, ref NetSegment otherSeg) => {
                    int index0 = segmentEndManager.GetIndex(otherSegId,otherSeg.m_startNode == nodeId);

                    if (otherSegId != segmentId
                        && segmentEndManager.ExtSegmentEnds[index0].outgoing)
                    {
                        hasOutgoingSegment = true;
                        return false;
                    }

                    return true;
                });

            // check if traffic can flow to the node and that there is at least one left segment
            if (!end.incoming || !hasOutgoingSegment) {
                if (logTurnOnRed) {
                    Log._Debug($"TurnOnRedManager.UpdateSegmentEnd({end.segmentId}, {end.startNode}): " +
                               "outgoing one-way or insufficient number of outgoing segments.");
                }

                return;
            }

            bool lhd = Services.SimulationService.LeftHandDrive;

            // check node
            // note that we must not check for the `TrafficLights` flag here because the flag might not be loaded yet
            bool nodeValid = false;
            Services.NetService.ProcessNode(
                nodeId,
                (ushort _, ref NetNode node) => {
                    nodeValid =
                        (node.m_flags & NetNode.Flags.LevelCrossing) ==
                        NetNode.Flags.None &&
                        node.Info?.m_class?.m_service != ItemClass.Service.Beautification;
                    return true;
                });

            if (!nodeValid) {
                if (logTurnOnRed) {
                    Log._Debug($"TurnOnRedManager.UpdateSegmentEnd({end.segmentId}, {end.startNode}): node invalid");
                }

                return;
            }

            // get left/right segments
            ushort leftSegmentId = 0;
            ushort rightSegmentId = 0;
            Services.NetService.ProcessSegment(
                end.segmentId,
                (ushort _, ref NetSegment segment) => {
                    segment.GetLeftAndRightSegments(
                        nodeId,
                        out leftSegmentId,
                        out rightSegmentId);
                    return true;
                });

            if (logTurnOnRed) {
                Log._Debug(
                    $"TurnOnRedManager.UpdateSegmentEnd({end.segmentId}, {end.startNode}): " +
                    $"got left/right segments: {leftSegmentId}/{rightSegmentId}");
            }

            // validate left/right segments according to geometric properties
            if (leftSegmentId != 0
                && segmentEndManager.GetDirection(ref end, leftSegmentId) != ArrowDirection.Left)
            {
                if (logTurnOnRed) {
                    Log._Debug(
                        $"TurnOnRedManager.UpdateSegmentEnd({end.segmentId}, {end.startNode}): " +
                        "left segment is not geometrically left");
                }

                leftSegmentId = 0;
            }

            if (rightSegmentId != 0
                && segmentEndManager.GetDirection(ref end, rightSegmentId) != ArrowDirection.Right)
            {
                if (logTurnOnRed) {
                    Log._Debug($"TurnOnRedManager.UpdateSegmentEnd({end.segmentId}, {end.startNode}): " +
                               "right segment is not geometrically right");
                }

                rightSegmentId = 0;
            }

            // check for incoming one-ways
            int indexL = segmentEndManager.GetIndex(leftSegmentId, nodeId);
            if (leftSegmentId != 0
                && !segmentEndManager.ExtSegmentEnds[indexL].outgoing)
            {
                if (logTurnOnRed) {
                    Log._Debug($"TurnOnRedManager.UpdateSegmentEnd({end.segmentId}, {end.startNode}): " +
                               "left segment is incoming one-way");
                }

                leftSegmentId = 0;
            }

            int indexR = segmentEndManager.GetIndex(rightSegmentId, nodeId);
            if (rightSegmentId != 0
                && !segmentEndManager.ExtSegmentEnds[indexR].outgoing)
            {
                if (logTurnOnRed) {
                    Log._Debug($"TurnOnRedManager.UpdateSegmentEnd({end.segmentId}, {end.startNode}): " +
                               "right segment is incoming one-way");
                }

                rightSegmentId = 0;
            }

            if (seg.oneWay) {
                if ((lhd && rightSegmentId != 0) || (!lhd && leftSegmentId != 0)) {
                    // special case: one-way to one-way in non-preferred direction
                    if (logTurnOnRed) {
                        Log._Debug(
                            $"TurnOnRedManager.UpdateSegmentEnd({end.segmentId}, {end.startNode}): " +
                            "source is incoming one-way. checking for one-way in non-preferred direction");
                    }

                    ushort targetSegmentId = lhd ? rightSegmentId : leftSegmentId;

                    if (!segmentManager.ExtSegments[targetSegmentId].oneWay) {
                        // disallow turn in non-preferred direction
                        if (logTurnOnRed) {
                            Log._Debug(
                                $"TurnOnRedManager.UpdateSegmentEnd({end.segmentId}, {end.startNode}): " +
                                $"turn in non-preferred direction {(lhd ? "right" : "left")} disallowed");
                        }

                        if (lhd) {
                            rightSegmentId = 0;
                        } else {
                            leftSegmentId = 0;
                        }
                    }
                }
            } else if (lhd) {
                // default case (LHD): turn in preferred direction
                rightSegmentId = 0;
            } else {
                // default case (RHD): turn in preferred direction
                leftSegmentId = 0;
            }

            int index = GetIndex(end.segmentId, end.startNode);
            TurnOnRedSegments[index].leftSegmentId = leftSegmentId;
            TurnOnRedSegments[index].rightSegmentId = rightSegmentId;

            if (logTurnOnRed) {
                Log._Debug(
                    $"TurnOnRedManager.UpdateSegmentEnd({end.segmentId}, {end.startNode}): " +
                    $"Finished calculation. leftSegmentId={leftSegmentId}, rightSegmentId={rightSegmentId}");
            }
        }

        protected void ResetSegment(ushort segmentId) {
            TurnOnRedSegments[GetIndex(segmentId, true)].Reset();
            TurnOnRedSegments[GetIndex(segmentId, false)].Reset();
        }
    }
}