﻿namespace TrafficManager.Custom.PathFinding {
    using System;
    using System.Threading;
    using ColossalFramework;
    using ColossalFramework.Math;
    using ColossalFramework.UI;
    using JetBrains.Annotations;
    using UnityEngine;

    public class StockPathFind : MonoBehaviour {
        private const float BYTE_TO_FLOAT_OFFSET_CONVERSION_FACTOR = 0.003921569f;
        private const float TICKET_COST_CONVERSION_FACTOR = BYTE_TO_FLOAT_OFFSET_CONVERSION_FACTOR * 0.0001f;

        private struct BufferItem {
            public PathUnit.Position m_position;
            public float m_comparisonValue;
            public float m_methodDistance;
            public float m_duration;
            public uint m_laneID;
            public NetInfo.Direction m_direction;
            public NetInfo.LaneType m_lanesUsed;
        }

        private Array32<PathUnit> m_pathUnits;
        private uint m_queueFirst;
        private uint m_queueLast;
        private uint m_calculating;
        private object m_queueLock;
        private Thread m_pathFindThread;
        private bool m_terminated;
        public ThreadProfiler m_pathfindProfiler;
        public volatile int m_queuedPathFindCount;
        private object m_bufferLock;
        private int m_bufferMinPos;
        private int m_bufferMaxPos;
        private uint[] m_laneLocation;
        private PathUnit.Position[] m_laneTarget;
        private BufferItem[] m_buffer;
        private int[] m_bufferMin;
        private int[] m_bufferMax;
        private float m_maxLength;
        private uint m_startLaneA;
        private uint m_startLaneB;
        private uint m_endLaneA;
        private uint m_endLaneB;
        private uint m_vehicleLane;
        private byte m_startOffsetA;
        private byte m_startOffsetB;
        private byte m_vehicleOffset;
        private NetSegment.Flags m_carBanMask;
        private bool m_ignoreBlocked;
        private bool m_stablePath;
        private bool m_randomParking;
        private bool m_transportVehicle;
        private bool m_ignoreCost;
        private NetSegment.Flags m_disableMask;
        private Randomizer m_pathRandomizer;
        private uint m_pathFindIndex;
        private NetInfo.LaneType m_laneTypes;
        private VehicleInfo.VehicleType m_vehicleTypes;

        [UsedImplicitly]
        public bool IsAvailable {
            get {
                return m_pathFindThread.IsAlive;
            }
        }

        private void Awake() {
            m_pathfindProfiler = new ThreadProfiler();
            m_laneLocation = new uint[262144];
            m_laneTarget = new PathUnit.Position[262144];
            m_buffer = new BufferItem[65536];
            m_bufferMin = new int[1024];
            m_bufferMax = new int[1024];
            m_queueLock = new object();
            m_bufferLock = Singleton<PathManager>.instance.m_bufferLock;
            m_pathUnits = Singleton<PathManager>.instance.m_pathUnits;
            m_pathFindThread = new Thread(PathFindThread);
            m_pathFindThread.Name = "Pathfind";
            m_pathFindThread.Priority = SimulationManager.SIMULATION_PRIORITY;
            m_pathFindThread.Start();
            if (!m_pathFindThread.IsAlive) {
                CODebugBase<LogChannel>.Error(LogChannel.Core, "Path find thread failed to start!");
            }
        }

        private void OnDestroy() {
            while (!Monitor.TryEnter(m_queueLock, SimulationManager.SYNCHRONIZE_TIMEOUT)) {
            }

            try {
                m_terminated = true;
                Monitor.PulseAll(m_queueLock);
            } finally {
                Monitor.Exit(m_queueLock);
            }
        }

        [UsedImplicitly]
        public bool CalculatePath(uint unit, bool skipQueue) {
            if (!Singleton<PathManager>.instance.AddPathReference(unit)) {
                return false;
            }

            while (!Monitor.TryEnter(m_queueLock, SimulationManager.SYNCHRONIZE_TIMEOUT)) {
            }

            try {
                if (skipQueue) {
                    if (m_queueLast == 0) {
                        m_queueLast = unit;
                    } else {
                        m_pathUnits.m_buffer[unit].m_nextPathUnit = m_queueFirst;
                    }

                    m_queueFirst = unit;
                } else {
                    if (m_queueLast == 0) {
                        m_queueFirst = unit;
                    } else {
                        m_pathUnits.m_buffer[m_queueLast].m_nextPathUnit = unit;
                    }

                    m_queueLast = unit;
                }

                m_pathUnits.m_buffer[unit].m_pathFindFlags |= 1;
                m_queuedPathFindCount++;

                Monitor.Pulse(m_queueLock);
            } finally {
                Monitor.Exit(m_queueLock);
            }

            return true;
        }

        public void WaitForAllPaths() {
            while (!Monitor.TryEnter(m_queueLock, SimulationManager.SYNCHRONIZE_TIMEOUT)) {
            }

            try {
                while (true) {
                    if (m_queueFirst == 0 && m_calculating == 0) {
                        break;
                    }

                    if (!m_terminated) {
                        Monitor.Wait(m_queueLock);
                        continue;
                    }

                    break;
                }
            } finally {
                Monitor.Exit(m_queueLock);
            }
        }

        private void PathFindImplementation(uint unit, ref PathUnit data) {
            var netManager = Singleton<NetManager>.instance;

            m_laneTypes = (NetInfo.LaneType)m_pathUnits.m_buffer[unit].m_laneTypes;
            m_vehicleTypes = (VehicleInfo.VehicleType)m_pathUnits.m_buffer[unit].m_vehicleTypes;
            m_maxLength = m_pathUnits.m_buffer[unit].m_length;
            m_pathFindIndex = m_pathFindIndex + 1 & 0x7FFF;
            m_pathRandomizer = new Randomizer(unit);
            m_carBanMask = NetSegment.Flags.CarBan;

            if ((m_pathUnits.m_buffer[unit].m_simulationFlags & PathUnit.FLAG_IS_HEAVY) != 0) {
                m_carBanMask |= NetSegment.Flags.HeavyBan;
            }

            if ((m_pathUnits.m_buffer[unit].m_simulationFlags & PathUnit.FLAG_READY) != 0) {
                m_carBanMask |= NetSegment.Flags.WaitingPath;
            }

            m_ignoreBlocked = (m_pathUnits.m_buffer[unit].m_simulationFlags & PathUnit.FLAG_IGNORE_BLOCKED) != 0;
            m_stablePath = (m_pathUnits.m_buffer[unit].m_simulationFlags & PathUnit.FLAG_STABLE_PATH) != 0;
            m_randomParking = (m_pathUnits.m_buffer[unit].m_simulationFlags & PathUnit.FLAG_RANDOM_PARKING) != 0;
            m_transportVehicle = (m_laneTypes & NetInfo.LaneType.TransportVehicle) != NetInfo.LaneType.None;
            m_ignoreCost = m_stablePath || (m_pathUnits.m_buffer[unit].m_simulationFlags & PathUnit.FLAG_IGNORE_COST) != 0;
            m_disableMask = NetSegment.Flags.Collapsed | NetSegment.Flags.PathFailed;

            if ((m_pathUnits.m_buffer[unit].m_simulationFlags & PathUnit.FLAG_IGNORE_FLOODED) == 0) {
                m_disableMask |= NetSegment.Flags.Flooded;
            }

            if ((m_laneTypes & NetInfo.LaneType.Vehicle) != NetInfo.LaneType.None) {
                m_laneTypes |= NetInfo.LaneType.TransportVehicle;
            }

            var posCount = m_pathUnits.m_buffer[unit].m_positionCount & 0xF;
            var vehiclePosIndicator = m_pathUnits.m_buffer[unit].m_positionCount >> 4;
            var bufferItemStartA = default(BufferItem);
            if (data.m_position00.m_segment != 0 && posCount >= 1) {
                m_startLaneA = PathManager.GetLaneID(data.m_position00);
                m_startOffsetA = data.m_position00.m_offset;
                bufferItemStartA.m_laneID = m_startLaneA;
                bufferItemStartA.m_position = data.m_position00;
                GetLaneDirection(data.m_position00, out bufferItemStartA.m_direction, out bufferItemStartA.m_lanesUsed);
                bufferItemStartA.m_comparisonValue = 0f;
                bufferItemStartA.m_duration = 0f;
            } else {
                m_startLaneA = 0u;
                m_startOffsetA = 0;
            }

            var bufferItemStartB = default(BufferItem);
            if (data.m_position02.m_segment != 0 && posCount >= 3) {
                m_startLaneB = PathManager.GetLaneID(data.m_position02);
                m_startOffsetB = data.m_position02.m_offset;
                bufferItemStartB.m_laneID = m_startLaneB;
                bufferItemStartB.m_position = data.m_position02;
                GetLaneDirection(data.m_position02, out bufferItemStartB.m_direction, out bufferItemStartB.m_lanesUsed);
                bufferItemStartB.m_comparisonValue = 0f;
                bufferItemStartB.m_duration = 0f;
            } else {
                m_startLaneB = 0u;
                m_startOffsetB = 0;
            }

            var bufferItemEndA = default(BufferItem);
            if (data.m_position01.m_segment != 0 && posCount >= 2) {
                m_endLaneA = PathManager.GetLaneID(data.m_position01);
                bufferItemEndA.m_laneID = m_endLaneA;
                bufferItemEndA.m_position = data.m_position01;
                GetLaneDirection(data.m_position01, out bufferItemEndA.m_direction, out bufferItemEndA.m_lanesUsed);
                bufferItemEndA.m_methodDistance = 0.01f;
                bufferItemEndA.m_comparisonValue = 0f;
                bufferItemEndA.m_duration = 0f;
            } else {
                m_endLaneA = 0u;
            }

            var bufferItemEndB = default(BufferItem);
            if (data.m_position03.m_segment != 0 && posCount >= 4) {
                m_endLaneB = PathManager.GetLaneID(data.m_position03);
                bufferItemEndB.m_laneID = m_endLaneB;
                bufferItemEndB.m_position = data.m_position03;
                GetLaneDirection(data.m_position03, out bufferItemEndB.m_direction, out bufferItemEndB.m_lanesUsed);
                bufferItemEndB.m_methodDistance = 0.01f;
                bufferItemEndB.m_comparisonValue = 0f;
                bufferItemEndB.m_duration = 0f;
            } else {
                m_endLaneB = 0u;
            }

            if (data.m_position11.m_segment != 0 && vehiclePosIndicator >= 1) {
                m_vehicleLane = PathManager.GetLaneID(data.m_position11);
                m_vehicleOffset = data.m_position11.m_offset;
            } else {
                m_vehicleLane = 0u;
                m_vehicleOffset = 0;
            }

            var finalBufferItem = default(BufferItem);
            byte startOffset = 0;
            m_bufferMinPos = 0;
            m_bufferMaxPos = -1;

            if (m_pathFindIndex == 0) {
                var num3 = 4294901760u;
                for (var i = 0; i < 262144; i++) {
                    m_laneLocation[i] = num3;
                }
            }

            for (var j = 0; j < 1024; j++) {
                m_bufferMin[j] = 0;
                m_bufferMax[j] = -1;
            }

            if (bufferItemEndA.m_position.m_segment != 0) {
                m_bufferMax[0]++;
                m_buffer[++m_bufferMaxPos] = bufferItemEndA;
            }

            if (bufferItemEndB.m_position.m_segment != 0) {
                m_bufferMax[0]++;
                m_buffer[++m_bufferMaxPos] = bufferItemEndB;
            }

            var canFindPath = false;
            while (m_bufferMinPos <= m_bufferMaxPos) {
                var bufMin = m_bufferMin[m_bufferMinPos];
                var bufMax = m_bufferMax[m_bufferMinPos];

                if (bufMin > bufMax) {
                    m_bufferMinPos++;
                } else {
                    m_bufferMin[m_bufferMinPos] = bufMin + 1;
                    var candidateItem = m_buffer[(m_bufferMinPos << 6) + bufMin];
                    if (candidateItem.m_position.m_segment == bufferItemStartA.m_position.m_segment && candidateItem.m_position.m_lane == bufferItemStartA.m_position.m_lane) {
                        if ((candidateItem.m_direction & NetInfo.Direction.Forward) != NetInfo.Direction.None && candidateItem.m_position.m_offset >= m_startOffsetA) {
                            finalBufferItem = candidateItem;
                            startOffset = m_startOffsetA;
                            canFindPath = true;
                            break;
                        }

                        if ((candidateItem.m_direction & NetInfo.Direction.Backward) != NetInfo.Direction.None && candidateItem.m_position.m_offset <= m_startOffsetA) {
                            finalBufferItem = candidateItem;
                            startOffset = m_startOffsetA;
                            canFindPath = true;
                            break;
                        }
                    }

                    if (candidateItem.m_position.m_segment == bufferItemStartB.m_position.m_segment && candidateItem.m_position.m_lane == bufferItemStartB.m_position.m_lane) {
                        if ((candidateItem.m_direction & NetInfo.Direction.Forward) != NetInfo.Direction.None && candidateItem.m_position.m_offset >= m_startOffsetB) {
                            finalBufferItem = candidateItem;
                            startOffset = m_startOffsetB;
                            canFindPath = true;
                            break;
                        }

                        if ((candidateItem.m_direction & NetInfo.Direction.Backward) != NetInfo.Direction.None && candidateItem.m_position.m_offset <= m_startOffsetB) {
                            finalBufferItem = candidateItem;
                            startOffset = m_startOffsetB;
                            canFindPath = true;
                            break;
                        }
                    }

                    if ((candidateItem.m_direction & NetInfo.Direction.Forward) != NetInfo.Direction.None) {
                        var startNodeId = netManager.m_segments.m_buffer[candidateItem.m_position.m_segment].m_startNode;
                        ProcessItemMain(
                            candidateItem,
                            startNodeId,
                            ref netManager.m_nodes.m_buffer[startNodeId],
                            0,
                            false);
                    }

                    if ((candidateItem.m_direction & NetInfo.Direction.Backward) != NetInfo.Direction.None) {
                        var endNodeId = netManager.m_segments.m_buffer[candidateItem.m_position.m_segment].m_endNode;
                        ProcessItemMain(
                            candidateItem,
                            endNodeId,
                            ref netManager.m_nodes.m_buffer[endNodeId],
                            255,
                            false);
                    }

                    var numIter = 0;
                    var specialNodeId = netManager.m_lanes.m_buffer[candidateItem.m_laneID].m_nodes;
                    if (specialNodeId == 0) {
                        continue;
                    }

                    var startNode2 = netManager.m_segments.m_buffer[candidateItem.m_position.m_segment].m_startNode;
                    var endNode2 = netManager.m_segments.m_buffer[candidateItem.m_position.m_segment].m_endNode;
                    var nodesDisabled =
                        ((netManager.m_nodes.m_buffer[startNode2].m_flags |
                          netManager.m_nodes.m_buffer[endNode2].m_flags) & NetNode.Flags.Disabled) !=
                        NetNode.Flags.None;

                    while (specialNodeId != 0) {
                        var direction = NetInfo.Direction.None;
                        var laneOffset = netManager.m_nodes.m_buffer[specialNodeId].m_laneOffset;

                        if (laneOffset <= candidateItem.m_position.m_offset) {
                            direction |= NetInfo.Direction.Forward;
                        }

                        if (laneOffset >= candidateItem.m_position.m_offset) {
                            direction |= NetInfo.Direction.Backward;
                        }

                        if ((candidateItem.m_direction & direction) != NetInfo.Direction.None &&
                            (!nodesDisabled ||
                             (netManager.m_nodes.m_buffer[specialNodeId].m_flags & NetNode.Flags.Disabled) !=
                             NetNode.Flags.None)) {
                            ProcessItemMain(
                                candidateItem,
                                specialNodeId,
                                ref netManager.m_nodes.m_buffer[specialNodeId],
                                laneOffset,
                                true);
                        }

                        specialNodeId = netManager.m_nodes.m_buffer[specialNodeId].m_nextLaneNode;

                        if (++numIter == 32768) {
                            break;
                        }
                    }
                }
            }

            if (!canFindPath) {
                m_pathUnits.m_buffer[unit].m_pathFindFlags |= PathUnit.FLAG_FAILED;
            } else {
                var duration =
                    m_laneTypes != NetInfo.LaneType.Pedestrian &&
                    (m_laneTypes & NetInfo.LaneType.Pedestrian) != NetInfo.LaneType.None
                        ? finalBufferItem.m_duration
                        : finalBufferItem.m_methodDistance;
                m_pathUnits.m_buffer[unit].m_length = duration;
                m_pathUnits.m_buffer[unit].m_speed = (byte)Mathf.Clamp(
                    finalBufferItem.m_methodDistance * 100f / Mathf.Max(0.01f, finalBufferItem.m_duration),
                    0f,
                    255f);
                var currentPathUnitId = unit;
                var currentItemPositionCount = 0;
                var sumOfPositionCounts = 0;
                var currentPosition = finalBufferItem.m_position;
                if ((currentPosition.m_segment != bufferItemEndA.m_position.m_segment ||
                     currentPosition.m_lane != bufferItemEndA.m_position.m_lane ||
                     currentPosition.m_offset != bufferItemEndA.m_position.m_offset) &&
                    (currentPosition.m_segment != bufferItemEndB.m_position.m_segment ||
                     currentPosition.m_lane != bufferItemEndB.m_position.m_lane ||
                     currentPosition.m_offset != bufferItemEndB.m_position.m_offset)) {
                    if (startOffset != currentPosition.m_offset) {
                        var position2 = currentPosition;
                        position2.m_offset = startOffset;
                        m_pathUnits.m_buffer[currentPathUnitId].SetPosition(currentItemPositionCount++, position2);
                    }

                    m_pathUnits.m_buffer[currentPathUnitId].SetPosition(currentItemPositionCount++, currentPosition);
                    currentPosition = m_laneTarget[finalBufferItem.m_laneID];
                }

                for (var k = 0; k < 262144; k++) {
                    m_pathUnits.m_buffer[currentPathUnitId].SetPosition(currentItemPositionCount++, currentPosition);
                    if (currentPosition.m_segment == bufferItemEndA.m_position.m_segment &&
                        currentPosition.m_lane == bufferItemEndA.m_position.m_lane &&
                        currentPosition.m_offset == bufferItemEndA.m_position.m_offset) {
                        goto IL_0c87;
                    }

                    if (currentPosition.m_segment == bufferItemEndB.m_position.m_segment &&
                        currentPosition.m_lane == bufferItemEndB.m_position.m_lane &&
                        currentPosition.m_offset == bufferItemEndB.m_position.m_offset) {
                        goto IL_0c87;
                    }

                    if (currentItemPositionCount == 12) {
                        while (!Monitor.TryEnter(m_bufferLock, SimulationManager.SYNCHRONIZE_TIMEOUT)) { }

                        var createdPathUnitId = default(uint);
                        try {
                            if (m_pathUnits.CreateItem(out createdPathUnitId, ref m_pathRandomizer)) {
                                m_pathUnits.m_buffer[createdPathUnitId] = m_pathUnits.m_buffer[currentPathUnitId];
                                m_pathUnits.m_buffer[createdPathUnitId].m_referenceCount = 1;
                                m_pathUnits.m_buffer[createdPathUnitId].m_pathFindFlags = 4;
                                m_pathUnits.m_buffer[currentPathUnitId].m_nextPathUnit = createdPathUnitId;
                                m_pathUnits.m_buffer[currentPathUnitId].m_positionCount =
                                    (byte)currentItemPositionCount;
                                sumOfPositionCounts += currentItemPositionCount;
                                Singleton<PathManager>.instance.m_pathUnitCount = (int)(m_pathUnits.ItemCount() - 1);
                                goto end_IL_0dbc;
                            }

                            m_pathUnits.m_buffer[unit].m_pathFindFlags |= PathUnit.FLAG_FAILED;
                            return;
                            end_IL_0dbc: ;
                        }
                        finally {
                            Monitor.Exit(m_bufferLock);
                        }

                        currentPathUnitId = createdPathUnitId;
                        currentItemPositionCount = 0;
                    }

                    var laneID = PathManager.GetLaneID(currentPosition);
                    currentPosition = m_laneTarget[laneID];
                    continue;
IL_0c87:
                    m_pathUnits.m_buffer[currentPathUnitId].m_positionCount = (byte)currentItemPositionCount;
                    sumOfPositionCounts += currentItemPositionCount;
                    if (sumOfPositionCounts != 0) {
                        currentPathUnitId = m_pathUnits.m_buffer[unit].m_nextPathUnit;
                        currentItemPositionCount = m_pathUnits.m_buffer[unit].m_positionCount;
                        var numIter = 0;
                        while (currentPathUnitId != 0) {
                            m_pathUnits.m_buffer[currentPathUnitId].m_length =
                                duration * (sumOfPositionCounts - currentItemPositionCount) /
                                sumOfPositionCounts;
                            currentItemPositionCount += m_pathUnits.m_buffer[currentPathUnitId].m_positionCount;
                            currentPathUnitId = m_pathUnits.m_buffer[currentPathUnitId].m_nextPathUnit;
                            if (++numIter >= 262144) {
                                CODebugBase<LogChannel>.Error(
                                    LogChannel.Core,
                                    "Invalid list detected!\n" + Environment.StackTrace);
                                break;
                            }
                        }
                    }

                    m_pathUnits.m_buffer[unit].m_pathFindFlags |= PathUnit.FLAG_READY;
                    return;
                }

                m_pathUnits.m_buffer[unit].m_pathFindFlags |= PathUnit.FLAG_FAILED;
            }
        }

        // 1
        private void ProcessItemMain(BufferItem item,
                                     ushort nextNodeId,
                                     ref NetNode nextNode,
                                     byte connectOffset,
                                     bool isMiddle) {
            var netManager = Singleton<NetManager>.instance;
            var prevIsPedestrianLane = false;
            var prevIsBicycleLane = false;
            var prevIsCenterPlatform = false;
            var prevIsElevated = false;
            var prevRelSimilarLaneIndex = 0;
            var prevSegmentInfo = netManager.m_segments.m_buffer[item.m_position.m_segment].Info;
            if (item.m_position.m_lane < prevSegmentInfo.m_lanes.Length) {
                var prevLaneInfo = prevSegmentInfo.m_lanes[item.m_position.m_lane];
                prevIsPedestrianLane = prevLaneInfo.m_laneType == NetInfo.LaneType.Pedestrian;
                prevIsBicycleLane = prevLaneInfo.m_laneType == NetInfo.LaneType.Vehicle &&
                                    (prevLaneInfo.m_vehicleType & m_vehicleTypes) == VehicleInfo.VehicleType.Bicycle;
                prevIsCenterPlatform = prevLaneInfo.m_centerPlatform;
                prevIsElevated = prevLaneInfo.m_elevated;
                prevRelSimilarLaneIndex =
                    (prevLaneInfo.m_finalDirection & NetInfo.Direction.Forward) == NetInfo.Direction.None
                        ? prevLaneInfo.m_similarLaneCount - prevLaneInfo.m_similarLaneIndex - 1
                        : prevLaneInfo.m_similarLaneIndex;
            }

            if (isMiddle) {
                for (var i = 0; i < 8; i++) {
                    var nextSegmentId = nextNode.GetSegment(i);
                    if (nextSegmentId != 0) {
                        ProcessItemCosts(
                            item,
                            nextNodeId,
                            nextSegmentId,
                            ref netManager.m_segments.m_buffer[nextSegmentId],
                            ref prevRelSimilarLaneIndex,
                            connectOffset,
                            !prevIsPedestrianLane,
                            prevIsPedestrianLane);
                    }
                }
            } else if (prevIsPedestrianLane) {
                if (!prevIsElevated) {
                    var prevSegmentId = item.m_position.m_segment;
                    int prevLaneIndex = item.m_position.m_lane;
                    if (nextNode.Info.m_class.m_service != ItemClass.Service.Beautification) {
                        var canCrossStreet =
                            (nextNode.m_flags & (NetNode.Flags.End | NetNode.Flags.Bend | NetNode.Flags.Junction)) !=
                            NetNode.Flags.None;
                        var isOnCenterPlatform = prevIsCenterPlatform &&
                                                 (nextNode.m_flags & (NetNode.Flags.End | NetNode.Flags.Junction)) ==
                                                 NetNode.Flags.None;
                        var nextLeftSegmentId = prevSegmentId;
                        var nextRightSegmentId = prevSegmentId;

                        netManager.m_segments.m_buffer[prevSegmentId].GetLeftAndRightLanes(
                            nextNodeId,
                            NetInfo.LaneType.Pedestrian,
                            VehicleInfo.VehicleType.None,
                            prevLaneIndex,
                            isOnCenterPlatform,
                            out var leftLaneIndex,
                            out var rightLaneIndex,
                            out var leftLaneId,
                            out var rightLaneId);

                        if (leftLaneId == 0 || rightLaneId == 0) {
                            netManager.m_segments.m_buffer[prevSegmentId].GetLeftAndRightSegments(
                                nextNodeId,
                                out var leftSegmentId,
                                out var rightSegmentId);
                            var numIter = 0;
                            while (leftSegmentId != 0 && leftSegmentId != prevSegmentId && leftLaneId == 0) {
                                netManager.m_segments.m_buffer[leftSegmentId].GetLeftAndRightLanes(
                                    nextNodeId,
                                    NetInfo.LaneType.Pedestrian,
                                    VehicleInfo.VehicleType.None,
                                    -1,
                                    isOnCenterPlatform,
                                    out _,
                                    out var someRightLaneIndex,
                                    out _,
                                    out var someRightLaneId);

                                if (someRightLaneId != 0) {
                                    nextLeftSegmentId = leftSegmentId;
                                    leftLaneIndex = someRightLaneIndex;
                                    leftLaneId = someRightLaneId;
                                } else {
                                    leftSegmentId = netManager
                                                    .m_segments.m_buffer[leftSegmentId].GetLeftSegment(nextNodeId);
                                }

                                if (++numIter == 8) {
                                    break;
                                }
                            }

                            numIter = 0;
                            while (rightSegmentId != 0 && rightSegmentId != prevSegmentId && rightLaneId == 0) {
                                netManager.m_segments.m_buffer[rightSegmentId].GetLeftAndRightLanes(
                                    nextNodeId,
                                    NetInfo.LaneType.Pedestrian,
                                    VehicleInfo.VehicleType.None,
                                    -1,
                                    isOnCenterPlatform,
                                    out var someLeftLaneIndex,
                                    out _,
                                    out var someLeftLaneId,
                                    out _);

                                if (someLeftLaneId != 0) {
                                    nextRightSegmentId = rightSegmentId;
                                    rightLaneIndex = someLeftLaneIndex;
                                    rightLaneId = someLeftLaneId;
                                } else {
                                    rightSegmentId = netManager
                                                     .m_segments.m_buffer[rightSegmentId].GetRightSegment(nextNodeId);
                                }

                                if (++numIter == 8) {
                                    break;
                                }
                            }
                        }

                        if (leftLaneId != 0 &&
                            (nextLeftSegmentId != prevSegmentId || canCrossStreet || isOnCenterPlatform)) {
                            ProcessItemPedBicycle(
                                item,
                                nextNodeId,
                                nextLeftSegmentId,
                                ref netManager.m_segments.m_buffer[nextLeftSegmentId],
                                connectOffset,
                                connectOffset,
                                leftLaneIndex,
                                leftLaneId);
                        }

                        if (rightLaneId != 0 && rightLaneId != leftLaneId &&
                            (nextRightSegmentId != prevSegmentId || canCrossStreet || isOnCenterPlatform)) {
                            ProcessItemPedBicycle(
                                item,
                                nextNodeId,
                                nextRightSegmentId,
                                ref netManager.m_segments.m_buffer[nextRightSegmentId],
                                connectOffset,
                                connectOffset,
                                rightLaneIndex,
                                rightLaneId);
                        }

                        if ((m_vehicleTypes & VehicleInfo.VehicleType.Bicycle) != VehicleInfo.VehicleType.None &&
                            netManager.m_segments.m_buffer[prevSegmentId].GetClosestLane(
                                item.m_position.m_lane,
                                NetInfo.LaneType.Vehicle,
                                VehicleInfo.VehicleType.Bicycle,
                                out var nextLaneIndex,
                                out var nextLaneId)) {
                            ProcessItemPedBicycle(
                                item,
                                nextNodeId,
                                prevSegmentId,
                                ref netManager.m_segments.m_buffer[prevSegmentId],
                                connectOffset,
                                connectOffset,
                                nextLaneIndex,
                                nextLaneId);
                        }
                    } else {
                        for (var j = 0; j < 8; j++) {
                            var nextSegmentId = nextNode.GetSegment(j);
                            if (nextSegmentId != 0 && nextSegmentId != prevSegmentId) {
                                ProcessItemCosts(
                                    item,
                                    nextNodeId,
                                    nextSegmentId,
                                    ref netManager.m_segments.m_buffer[nextSegmentId],
                                    ref prevRelSimilarLaneIndex,
                                    connectOffset,
                                    false,
                                    true);
                            }
                        }
                    }

                    var nextLaneType = m_laneTypes & ~NetInfo.LaneType.Pedestrian;
                    var nextVehicleType = m_vehicleTypes & ~VehicleInfo.VehicleType.Bicycle;
                    if ((item.m_lanesUsed & (NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle)) !=
                        NetInfo.LaneType.None) {
                        nextLaneType &= ~(NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle);
                    }

                    if (nextLaneType != NetInfo.LaneType.None && nextVehicleType != VehicleInfo.VehicleType.None &&
                        netManager.m_segments.m_buffer[prevSegmentId].GetClosestLane(
                            prevLaneIndex,
                            nextLaneType,
                            nextVehicleType,
                            out var sameSegLaneIndex,
                            out var sameSegLaneId)) {
                        var sameSegLaneInfo = prevSegmentInfo.m_lanes[sameSegLaneIndex];
                        var sameSegConnectOffset =
                            (byte)((netManager.m_segments.m_buffer[prevSegmentId].m_flags & NetSegment.Flags.Invert) !=
                                   NetSegment.Flags.None ==
                                   ((sameSegLaneInfo.m_finalDirection & NetInfo.Direction.Backward) !=
                                    NetInfo.Direction.None)
                                       ? 1
                                       : 254);
                        var nextItem = item;
                        if (m_randomParking) {
                            nextItem.m_comparisonValue += m_pathRandomizer.Int32(300u) / m_maxLength;
                        }

                        ProcessItemPedBicycle(
                            nextItem,
                            nextNodeId,
                            prevSegmentId,
                            ref netManager.m_segments.m_buffer[prevSegmentId],
                            sameSegConnectOffset,
                            128,
                            sameSegLaneIndex,
                            sameSegLaneId);
                    }
                }
            } else {
                var allowPedestrian = (m_laneTypes & NetInfo.LaneType.Pedestrian) != NetInfo.LaneType.None;
                var allowBicycle = false;
                byte switchConnectOffset = 0;
                if (allowPedestrian) {
                    if (prevIsBicycleLane) {
                        switchConnectOffset = connectOffset;
                        allowBicycle = nextNode.Info.m_class.m_service == ItemClass.Service.Beautification;
                    } else if (m_vehicleLane != 0) {
                        if (m_vehicleLane != item.m_laneID) {
                            allowPedestrian = false;
                        } else {
                            switchConnectOffset = m_vehicleOffset;
                        }
                    } else {
                        switchConnectOffset = (byte)(!m_stablePath ? (byte)m_pathRandomizer.UInt32(1u, 254u) : 128);
                    }
                }

                ushort nextSegmentId;
                if ((m_vehicleTypes & (VehicleInfo.VehicleType.Ferry | VehicleInfo.VehicleType.Monorail)) !=
                    VehicleInfo.VehicleType.None) {
                    var isUturnAllowedHere =
                        (nextNode.m_flags & (NetNode.Flags.End | NetNode.Flags.Bend | NetNode.Flags.Junction)) !=
                        NetNode.Flags.None;
                    for (var k = 0; k < 8; k++) {
                        nextSegmentId = nextNode.GetSegment(k);
                        if (nextSegmentId != 0 && nextSegmentId != item.m_position.m_segment) {
                            ProcessItemCosts(
                                item,
                                nextNodeId,
                                nextSegmentId,
                                ref netManager.m_segments.m_buffer[nextSegmentId],
                                ref prevRelSimilarLaneIndex,
                                connectOffset,
                                true,
                                allowBicycle);
                        }
                    }

                    if (isUturnAllowedHere && (m_vehicleTypes & VehicleInfo.VehicleType.Monorail) ==
                        VehicleInfo.VehicleType.None) {
                        nextSegmentId = item.m_position.m_segment;
                        ProcessItemCosts(
                            item,
                            nextNodeId,
                            nextSegmentId,
                            ref netManager.m_segments.m_buffer[nextSegmentId],
                            ref prevRelSimilarLaneIndex,
                            connectOffset,
                            true,
                            false);
                    }
                } else {
                    var isUturnAllowedHere = (nextNode.m_flags & (NetNode.Flags.End | NetNode.Flags.OneWayOut)) !=
                                             NetNode.Flags.None;
                    nextSegmentId = netManager.m_segments.m_buffer[item.m_position.m_segment]
                                              .GetRightSegment(nextNodeId);
                    for (var l = 0; l < 8; l++) {
                        if (nextSegmentId == 0) {
                            break;
                        }

                        if (nextSegmentId == item.m_position.m_segment) {
                            break;
                        }

                        if (ProcessItemCosts(
                            item,
                            nextNodeId,
                            nextSegmentId,
                            ref netManager.m_segments.m_buffer[nextSegmentId],
                            ref prevRelSimilarLaneIndex,
                            connectOffset,
                            true,
                            allowBicycle)) {
                            isUturnAllowedHere = true;
                        }

                        nextSegmentId = netManager.m_segments.m_buffer[nextSegmentId].GetRightSegment(nextNodeId);
                    }

                    if (isUturnAllowedHere &&
                        (m_vehicleTypes & VehicleInfo.VehicleType.Tram) == VehicleInfo.VehicleType.None) {
                        nextSegmentId = item.m_position.m_segment;
                        ProcessItemCosts(
                            item,
                            nextNodeId,
                            nextSegmentId,
                            ref netManager.m_segments.m_buffer[nextSegmentId],
                            ref prevRelSimilarLaneIndex,
                            connectOffset,
                            true,
                            false);
                    }
                }

                if (allowPedestrian) {
                    nextSegmentId = item.m_position.m_segment;
                    if (netManager.m_segments.m_buffer[nextSegmentId].GetClosestLane(
                        item.m_position.m_lane,
                        NetInfo.LaneType.Pedestrian,
                        m_vehicleTypes,
                        out var nextLaneIndex,
                        out var nextLaneId)) {
                        ProcessItemPedBicycle(
                            item,
                            nextNodeId,
                            nextSegmentId,
                            ref netManager.m_segments.m_buffer[nextSegmentId],
                            switchConnectOffset,
                            switchConnectOffset,
                            nextLaneIndex,
                            nextLaneId);
                    }
                }
            }

            if (nextNode.m_lane != 0) {
                var targetDisabled = (nextNode.m_flags & (NetNode.Flags.Disabled | NetNode.Flags.DisableOnlyMiddle)) ==
                                     NetNode.Flags.Disabled;
                var nextSegmentId = netManager.m_lanes.m_buffer[nextNode.m_lane].m_segment;
                if (nextSegmentId != 0 && nextSegmentId != item.m_position.m_segment) {
                    ProcessItemPublicTransport(
                        item,
                        nextNodeId,
                        targetDisabled,
                        nextSegmentId,
                        ref netManager.m_segments.m_buffer[nextSegmentId],
                        nextNode.m_lane,
                        nextNode.m_laneOffset,
                        connectOffset);
                }
            }
        }

        // 2
        private void ProcessItemPublicTransport(BufferItem item,
                                                ushort nextNodeId,
                                                bool targetDisabled,
                                                ushort nextSegmentId,
                                                ref NetSegment nextSegment,
                                                uint nextLaneId,
                                                byte offset,
                                                byte connectOffset) {
            if ((nextSegment.m_flags & m_disableMask) != NetSegment.Flags.None) {
                return;
            }

            var netManager = Singleton<NetManager>.instance;
            if (targetDisabled &&
                ((netManager.m_nodes.m_buffer[nextSegment.m_startNode].m_flags |
                  netManager.m_nodes.m_buffer[nextSegment.m_endNode].m_flags) & NetNode.Flags.Disabled) ==
                NetNode.Flags.None) {
                return;
            }

            var nextSegmentInfo = nextSegment.Info;
            var prevSegmentInfo = netManager.m_segments.m_buffer[item.m_position.m_segment].Info;
            var nextNumLanes = nextSegmentInfo.m_lanes.Length;
            var curLaneId = nextSegment.m_lanes;
            var prevMaxSpeed = 1f;
            var prevSpeed = 1f;
            var prevLaneType = NetInfo.LaneType.None;
            if (item.m_position.m_lane < prevSegmentInfo.m_lanes.Length) {
                var prevLaneInfo = prevSegmentInfo.m_lanes[item.m_position.m_lane];
                prevMaxSpeed = prevLaneInfo.m_speedLimit;
                prevLaneType = prevLaneInfo.m_laneType;
                if ((prevLaneType & (NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle)) !=
                    NetInfo.LaneType.None) {
                    prevLaneType = NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle;
                }

                prevSpeed = CalculateLaneSpeed(
                    connectOffset,
                    item.m_position.m_offset,
                    ref netManager.m_segments.m_buffer[item.m_position.m_segment],
                    prevLaneInfo);
            }

            var prevLength = prevLaneType != NetInfo.LaneType.PublicTransport
                                 ? netManager.m_segments.m_buffer[item.m_position.m_segment].m_averageLength
                                 : netManager.m_lanes.m_buffer[item.m_laneID].m_length;
            var offsetLength = Mathf.Abs(connectOffset - item.m_position.m_offset) *
                               BYTE_TO_FLOAT_OFFSET_CONVERSION_FACTOR * prevLength;
            var methodDistance = item.m_methodDistance + offsetLength;
            var comparisonValue = item.m_comparisonValue + offsetLength / (prevSpeed * m_maxLength);
            var duration = item.m_duration + offsetLength / prevMaxSpeed;
            var b = netManager.m_lanes.m_buffer[item.m_laneID]
                              .CalculatePosition(connectOffset * BYTE_TO_FLOAT_OFFSET_CONVERSION_FACTOR);
            if (!m_ignoreCost) {
                int ticketCost = netManager.m_lanes.m_buffer[item.m_laneID].m_ticketCost;
                if (ticketCost != 0) {
                    comparisonValue += ticketCost * m_pathRandomizer.Int32(2000u) * TICKET_COST_CONVERSION_FACTOR;
                }
            }

            var nextLaneIndex = 0;
            while (true) {
                if (nextLaneIndex < nextNumLanes && curLaneId != 0) {
                    if (nextLaneId != curLaneId) {
                        curLaneId = netManager.m_lanes.m_buffer[curLaneId].m_nextLane;
                        nextLaneIndex++;
                        continue;
                    }

                    break;
                }

                return;
            }

            var nextLaneInfo = nextSegmentInfo.m_lanes[nextLaneIndex];
            if (nextLaneInfo.CheckType(m_laneTypes, m_vehicleTypes)) {
                var a = netManager.m_lanes.m_buffer[nextLaneId]
                                  .CalculatePosition(offset * BYTE_TO_FLOAT_OFFSET_CONVERSION_FACTOR);
                var distance = Vector3.Distance(a, b);
                var nextItem = default(BufferItem);

                nextItem.m_position.m_segment = nextSegmentId;
                nextItem.m_position.m_lane = (byte)nextLaneIndex;
                nextItem.m_position.m_offset = offset;

                if ((nextLaneInfo.m_laneType & prevLaneType) == NetInfo.LaneType.None) {
                    nextItem.m_methodDistance = 0f;
                } else {
                    nextItem.m_methodDistance = methodDistance + distance;
                }

                if (nextLaneInfo.m_laneType == NetInfo.LaneType.Pedestrian && !(nextItem.m_methodDistance < 1000f) &&
                    !m_stablePath) {
                    return;
                }

                nextItem.m_comparisonValue = comparisonValue +
                                             distance /
                                             ((prevMaxSpeed + nextLaneInfo.m_speedLimit) * 0.5f * m_maxLength);
                nextItem.m_duration = duration + distance / ((prevMaxSpeed + nextLaneInfo.m_speedLimit) * 0.5f);

                if ((nextSegment.m_flags & NetSegment.Flags.Invert) != NetSegment.Flags.None) {
                    nextItem.m_direction = NetInfo.InvertDirection(nextLaneInfo.m_finalDirection);
                } else {
                    nextItem.m_direction = nextLaneInfo.m_finalDirection;
                }

                if (nextLaneId == m_startLaneA) {
                    if (((nextItem.m_direction & NetInfo.Direction.Forward) == NetInfo.Direction.None ||
                         nextItem.m_position.m_offset < m_startOffsetA) &&
                        ((nextItem.m_direction & NetInfo.Direction.Backward) == NetInfo.Direction.None ||
                         nextItem.m_position.m_offset > m_startOffsetA)) {
                        return;
                    }

                    var nextSpeed = CalculateLaneSpeed(
                        m_startOffsetA,
                        nextItem.m_position.m_offset,
                        ref nextSegment,
                        nextLaneInfo);
                    var nextOffset = Mathf.Abs(nextItem.m_position.m_offset - m_startOffsetA) *
                                     BYTE_TO_FLOAT_OFFSET_CONVERSION_FACTOR;

                    nextItem.m_comparisonValue += nextOffset * nextSegment.m_averageLength / (nextSpeed * m_maxLength);
                    nextItem.m_duration += nextOffset * nextSegment.m_averageLength / nextSpeed;
                }

                if (nextLaneId == m_startLaneB) {
                    if (((nextItem.m_direction & NetInfo.Direction.Forward) == NetInfo.Direction.None ||
                         nextItem.m_position.m_offset < m_startOffsetB) &&
                        ((nextItem.m_direction & NetInfo.Direction.Backward) == NetInfo.Direction.None ||
                         nextItem.m_position.m_offset > m_startOffsetB)) {
                        return;
                    }

                    var nextSpeed = CalculateLaneSpeed(
                        m_startOffsetB,
                        nextItem.m_position.m_offset,
                        ref nextSegment,
                        nextLaneInfo);
                    var nextOffset = Mathf.Abs(nextItem.m_position.m_offset - m_startOffsetB) *
                                     BYTE_TO_FLOAT_OFFSET_CONVERSION_FACTOR;

                    nextItem.m_comparisonValue += nextOffset * nextSegment.m_averageLength / (nextSpeed * m_maxLength);
                    nextItem.m_duration += nextOffset * nextSegment.m_averageLength / nextSpeed;
                }

                nextItem.m_laneID = nextLaneId;
                nextItem.m_lanesUsed = item.m_lanesUsed | nextLaneInfo.m_laneType;

                AddBufferItem(nextItem, item.m_position);
            }
        }

        // 3
        private bool ProcessItemCosts(BufferItem item,
                                      ushort nextNodeId,
                                      ushort nextSegmentId,
                                      ref NetSegment nextSegment,
                                      ref int laneIndexFromInner,
                                      byte connectOffset,
                                      bool enableVehicle,
                                      bool enablePedestrian) {
            var blocked = false;
            if ((nextSegment.m_flags & m_disableMask) != 0) {
                return blocked;
            }

            var netManager = Singleton<NetManager>.instance;
            var nextSegmentInfo = nextSegment.Info;
            var prevSegmentInfo = netManager.m_segments.m_buffer[item.m_position.m_segment].Info;
            var nextNumLanes = nextSegmentInfo.m_lanes.Length;
            var curLaneId = nextSegment.m_lanes;
            var nextDir = nextNodeId != nextSegment.m_startNode
                              ? NetInfo.Direction.Forward
                              : NetInfo.Direction.Backward;
            var nextFinalDir = (nextSegment.m_flags & NetSegment.Flags.Invert) == NetSegment.Flags.None
                                   ? nextDir
                                   : NetInfo.InvertDirection(nextDir);
            var prevMaxSpeed = 1f;
            var prevLaneSpeed = 1f;
            var prevLaneType = NetInfo.LaneType.None;
            var prevVehicleType = VehicleInfo.VehicleType.None;
            if (item.m_position.m_lane < prevSegmentInfo.m_lanes.Length) {
                var prevLaneInfo = prevSegmentInfo.m_lanes[item.m_position.m_lane];
                prevLaneType = prevLaneInfo.m_laneType;
                prevVehicleType = prevLaneInfo.m_vehicleType;
                prevMaxSpeed = prevLaneInfo.m_speedLimit;
                prevLaneSpeed = CalculateLaneSpeed(
                    connectOffset,
                    item.m_position.m_offset,
                    ref netManager.m_segments.m_buffer[item.m_position.m_segment],
                    prevLaneInfo);
            }

            var acuteTurningAngle = false;
            if (prevLaneType == NetInfo.LaneType.Vehicle &&
                (prevVehicleType & VehicleInfo.VehicleType.Car) == VehicleInfo.VehicleType.None) {
                var turningAngle = 0.01f - Mathf.Min(
                                       nextSegmentInfo.m_maxTurnAngleCos,
                                       prevSegmentInfo.m_maxTurnAngleCos);
                if (turningAngle < 1f) {
                    var vector = nextNodeId != netManager.m_segments.m_buffer[item.m_position.m_segment].m_startNode
                                     ? netManager.m_segments.m_buffer[item.m_position.m_segment].m_endDirection
                                     : netManager.m_segments.m_buffer[item.m_position.m_segment].m_startDirection;
                    var vector2 = (nextDir & NetInfo.Direction.Forward) == NetInfo.Direction.None
                                      ? nextSegment.m_startDirection
                                      : nextSegment.m_endDirection;
                    var dirDotProd = (vector.x * vector2.x) + (vector.z * vector2.z);
                    if (dirDotProd >= turningAngle) {
                        acuteTurningAngle = true;
                    }
                }
            }

            var prevLength = prevLaneType != NetInfo.LaneType.PublicTransport
                                 ? netManager.m_segments.m_buffer[item.m_position.m_segment].m_averageLength
                                 : netManager.m_lanes.m_buffer[item.m_laneID].m_length;
            var offsetLength = Mathf.Abs(connectOffset - item.m_position.m_offset)
                               * BYTE_TO_FLOAT_OFFSET_CONVERSION_FACTOR * prevLength;
            var methodDistance = item.m_methodDistance + offsetLength;
            var duration = item.m_duration + (offsetLength / prevMaxSpeed);

            if (!m_stablePath) {
                offsetLength *= (new Randomizer(m_pathFindIndex << 16 | item.m_position.m_segment)
                                     .Int32(
                                         900,
                                         1000 + netManager
                                                .m_segments.m_buffer[item.m_position.m_segment]
                                                .m_trafficDensity * 10) +
                                 m_pathRandomizer.Int32(20u)) * 0.001f;
            }

            if ((prevLaneType & (NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle)) !=
                NetInfo.LaneType.None && (prevVehicleType & m_vehicleTypes) == VehicleInfo.VehicleType.Car &&
                (netManager.m_segments.m_buffer[item.m_position.m_segment].m_flags & m_carBanMask) !=
                NetSegment.Flags.None) {
                offsetLength *= 7.5f;
            }

            if (m_transportVehicle && prevLaneType == NetInfo.LaneType.TransportVehicle) {
                offsetLength *= 0.95f;
            }

            var comparisonValue = item.m_comparisonValue + (offsetLength / (prevLaneSpeed * m_maxLength));
            int ticketCost = netManager.m_lanes.m_buffer[item.m_laneID].m_ticketCost;
            if (!m_ignoreCost && ticketCost != 0) {
                comparisonValue += ticketCost * m_pathRandomizer.Int32(2000u) * TICKET_COST_CONVERSION_FACTOR;
            }

            if ((prevLaneType & (NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle)) != 0) {
                prevLaneType = NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle;
            }

            var b = netManager.m_lanes.m_buffer[item.m_laneID]
                              .CalculatePosition(connectOffset * BYTE_TO_FLOAT_OFFSET_CONVERSION_FACTOR);
            var newLaneIndexFromInner = laneIndexFromInner;
            var transitionNode = (netManager.m_nodes.m_buffer[nextNodeId].m_flags & NetNode.Flags.Transition) !=
                                 NetNode.Flags.None;
            var allowedLaneTypes = m_laneTypes;
            var allowedVehicleTypes = m_vehicleTypes;
            if (!enableVehicle) {
                allowedVehicleTypes &= VehicleInfo.VehicleType.Bicycle;
                if (allowedVehicleTypes == VehicleInfo.VehicleType.None) {
                    allowedLaneTypes &= ~(NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle);
                }
            }

            if (!enablePedestrian) {
                allowedLaneTypes &= ~NetInfo.LaneType.Pedestrian;
            }

            for (var i = 0; i < nextNumLanes && curLaneId != 0; i++) {
                var nextLaneInfo = nextSegmentInfo.m_lanes[i];
                float transitionCost;
                var nextItem = default(BufferItem);
                if ((nextLaneInfo.m_finalDirection & nextFinalDir) != 0) {
                    if (nextLaneInfo.CheckType(allowedLaneTypes, allowedVehicleTypes) &&
                        (nextSegmentId != item.m_position.m_segment || i != item.m_position.m_lane) &&
                        (nextLaneInfo.m_finalDirection & nextFinalDir) != 0) {
                        if (acuteTurningAngle && nextLaneInfo.m_laneType == NetInfo.LaneType.Vehicle &&
                            (nextLaneInfo.m_vehicleType & VehicleInfo.VehicleType.Car) ==
                            VehicleInfo.VehicleType.None) {
                            continue;
                        }

                        var a = (nextDir & NetInfo.Direction.Forward) == NetInfo.Direction.None
                                    ? netManager.m_lanes.m_buffer[curLaneId].m_bezier.a
                                    : netManager.m_lanes.m_buffer[curLaneId].m_bezier.d;
                        transitionCost = Vector3.Distance(a, b);
                        if (transitionNode) {
                            transitionCost *= 2f;
                        }

                        if (ticketCost != 0 && netManager.m_lanes.m_buffer[curLaneId].m_ticketCost != 0) {
                            transitionCost *= 10f;
                        }

                        var transitionCostOverMeanMaxSpeed =
                            transitionCost / ((prevMaxSpeed + nextLaneInfo.m_speedLimit) * 0.5f * m_maxLength);

                        if (!m_stablePath && (netManager.m_lanes.m_buffer[curLaneId].m_flags & 0x80) != 0) {
                            int firstTarget = netManager.m_lanes.m_buffer[curLaneId].m_firstTarget;
                            int lastTarget = netManager.m_lanes.m_buffer[curLaneId].m_lastTarget;
                            transitionCostOverMeanMaxSpeed *=
                                new Randomizer(m_pathFindIndex ^ curLaneId).Int32(
                                    1000,
                                    (lastTarget - firstTarget + 2) * 1000) * 0.001f;
                        }

                        nextItem.m_position.m_segment = nextSegmentId;
                        nextItem.m_position.m_lane = (byte)i;
                        nextItem.m_position.m_offset = (byte)((nextDir & NetInfo.Direction.Forward) != 0 ? 255 : 0);

                        if ((nextLaneInfo.m_laneType & prevLaneType) == NetInfo.LaneType.None) {
                            nextItem.m_methodDistance = 0f;
                        } else {
                            nextItem.m_methodDistance = methodDistance + transitionCost;
                        }

                        if (nextLaneInfo.m_laneType == NetInfo.LaneType.Pedestrian &&
                            !(nextItem.m_methodDistance < 1000f) && !m_stablePath) {
                            goto IL_09e6;
                        }

                        nextItem.m_comparisonValue = comparisonValue + transitionCostOverMeanMaxSpeed;
                        nextItem.m_duration = duration + (transitionCost
                                                          / ((prevMaxSpeed + nextLaneInfo.m_speedLimit) * 0.5f));
                        nextItem.m_direction = nextDir;

                        if (curLaneId == m_startLaneA) {
                            if ((nextItem.m_direction & NetInfo.Direction.Forward) != NetInfo.Direction.None &&
                                nextItem.m_position.m_offset >= m_startOffsetA) {
                                goto IL_06c5;
                            }

                            if ((nextItem.m_direction & NetInfo.Direction.Backward) != NetInfo.Direction.None &&
                                nextItem.m_position.m_offset <= m_startOffsetA) {
                                goto IL_06c5;
                            }

                            curLaneId = netManager.m_lanes.m_buffer[curLaneId].m_nextLane;
                            continue;
                        }

                        goto IL_0765;
                    }
                } else if ((nextLaneInfo.m_laneType & prevLaneType) != NetInfo.LaneType.None &&
                           (nextLaneInfo.m_vehicleType & prevVehicleType) != VehicleInfo.VehicleType.None) {
                    newLaneIndexFromInner++;
                }

                goto IL_09e6;
                IL_06c5:
                var nextLaneSpeed = CalculateLaneSpeed(
                    m_startOffsetA,
                    nextItem.m_position.m_offset,
                    ref nextSegment,
                    nextLaneInfo);
                var nextOffset = Mathf.Abs(nextItem.m_position.m_offset - m_startOffsetA)
                                 * BYTE_TO_FLOAT_OFFSET_CONVERSION_FACTOR;
                nextItem.m_comparisonValue += nextOffset * nextSegment.m_averageLength
                                              / (nextLaneSpeed * m_maxLength);
                nextItem.m_duration += nextOffset * nextSegment.m_averageLength / nextLaneSpeed;
                goto IL_0765;
                IL_09e6:
                curLaneId = netManager.m_lanes.m_buffer[curLaneId].m_nextLane;
                continue;
                IL_085e:
                if (!m_ignoreBlocked && (nextSegment.m_flags & NetSegment.Flags.Blocked) != NetSegment.Flags.None &&
                    (nextLaneInfo.m_laneType & (NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle)) !=
                    NetInfo.LaneType.None) {
                    nextItem.m_comparisonValue += 0.1f;
                    blocked = true;
                }

                nextItem.m_lanesUsed = item.m_lanesUsed | nextLaneInfo.m_laneType;
                nextItem.m_laneID = curLaneId;
                if ((nextLaneInfo.m_laneType & prevLaneType) != NetInfo.LaneType.None &&
                    (nextLaneInfo.m_vehicleType & m_vehicleTypes) != VehicleInfo.VehicleType.None) {
                    int firstTarget = netManager.m_lanes.m_buffer[curLaneId].m_firstTarget;
                    int lastTarget = netManager.m_lanes.m_buffer[curLaneId].m_lastTarget;
                    if (laneIndexFromInner < firstTarget || laneIndexFromInner >= lastTarget) {
                        nextItem.m_comparisonValue += Mathf.Max(1f, transitionCost * 3f - 3f) /
                                                      ((prevMaxSpeed + nextLaneInfo.m_speedLimit) * 0.5f * m_maxLength);
                    }

                    if (!m_transportVehicle && nextLaneInfo.m_laneType == NetInfo.LaneType.TransportVehicle) {
                        nextItem.m_comparisonValue +=
                            20f / ((prevMaxSpeed + nextLaneInfo.m_speedLimit) * 0.5f * m_maxLength);
                    }
                }

                AddBufferItem(nextItem, item.m_position);
                goto IL_09e6;
                IL_0765:
                if (curLaneId == m_startLaneB) {
                    if ((nextItem.m_direction & NetInfo.Direction.Forward) != NetInfo.Direction.None &&
                        nextItem.m_position.m_offset >= m_startOffsetB) {
                        goto IL_07be;
                    }

                    if ((nextItem.m_direction & NetInfo.Direction.Backward) != NetInfo.Direction.None &&
                        nextItem.m_position.m_offset <= m_startOffsetB) {
                        goto IL_07be;
                    }

                    curLaneId = netManager.m_lanes.m_buffer[curLaneId].m_nextLane;
                    continue;
                }

                goto IL_085e;
                IL_07be:
                var nextLaneSpeed2 = CalculateLaneSpeed(
                    m_startOffsetB,
                    nextItem.m_position.m_offset,
                    ref nextSegment,
                    nextLaneInfo);
                var nextOffset2 = Mathf.Abs(nextItem.m_position.m_offset - m_startOffsetB) *
                                  BYTE_TO_FLOAT_OFFSET_CONVERSION_FACTOR;
                nextItem.m_comparisonValue +=
                    nextOffset2 * nextSegment.m_averageLength / (nextLaneSpeed2 * m_maxLength);
                nextItem.m_duration += nextOffset2 * nextSegment.m_averageLength / nextLaneSpeed2;
                goto IL_085e;
            }

            laneIndexFromInner = newLaneIndexFromInner;
            return blocked;
        }

        // 4
        private void ProcessItemPedBicycle(BufferItem item, ushort nextNodeId, ushort nextSegmentId, ref NetSegment nextSegment, byte connectOffset, byte laneSwitchOffset, int nextLaneIndex, uint nextLaneId) {
            if ((nextSegment.m_flags & m_disableMask) != NetSegment.Flags.None) {
                return;
            }

            var netManager = Singleton<NetManager>.instance;
            var nextSegmentInfo = nextSegment.Info;
            var prevSegmentInfo = netManager.m_segments.m_buffer[item.m_position.m_segment].Info;
            var nextNumLanes = nextSegmentInfo.m_lanes.Length;
            float distance;
            byte offset;
            if (nextSegmentId == item.m_position.m_segment) {
                var b = netManager.m_lanes.m_buffer[item.m_laneID].CalculatePosition(laneSwitchOffset * BYTE_TO_FLOAT_OFFSET_CONVERSION_FACTOR);
                var a = netManager.m_lanes.m_buffer[nextLaneId].CalculatePosition(connectOffset * BYTE_TO_FLOAT_OFFSET_CONVERSION_FACTOR);
                distance = Vector3.Distance(a, b);
                offset = connectOffset;
            } else {
                var direction = (NetInfo.Direction)(nextNodeId != nextSegment.m_startNode ? 1 : 2);
                var b = netManager.m_lanes.m_buffer[item.m_laneID].CalculatePosition(laneSwitchOffset * BYTE_TO_FLOAT_OFFSET_CONVERSION_FACTOR);
                var a = (direction & NetInfo.Direction.Forward) == NetInfo.Direction.None ? netManager.m_lanes.m_buffer[nextLaneId].m_bezier.a : netManager.m_lanes.m_buffer[nextLaneId].m_bezier.d;
                distance = Vector3.Distance(a, b);
                offset = (byte)((direction & NetInfo.Direction.Forward) != 0 ? 255 : 0);
            }

            var prevMaxSpeed = 1f;
            var prevSpeed = 1f;
            var prevLaneType = NetInfo.LaneType.None;
            if (item.m_position.m_lane < prevSegmentInfo.m_lanes.Length) {
                var prevLaneInfo = prevSegmentInfo.m_lanes[item.m_position.m_lane];
                prevMaxSpeed = prevLaneInfo.m_speedLimit;
                prevLaneType = prevLaneInfo.m_laneType;
                if ((prevLaneType & (NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle)) != NetInfo.LaneType.None) {
                    prevLaneType = NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle;
                }
                prevSpeed = CalculateLaneSpeed(laneSwitchOffset, item.m_position.m_offset, ref netManager.m_segments.m_buffer[item.m_position.m_segment], prevLaneInfo);
            }
            var prevLength = prevLaneType != NetInfo.LaneType.PublicTransport ? netManager.m_segments.m_buffer[item.m_position.m_segment].m_averageLength : netManager.m_lanes.m_buffer[item.m_laneID].m_length;
            var offsetLength = Mathf.Abs(laneSwitchOffset - item.m_position.m_offset) * BYTE_TO_FLOAT_OFFSET_CONVERSION_FACTOR * prevLength;
            var methodDistance = item.m_methodDistance + offsetLength;
            var comparisonValue = item.m_comparisonValue + offsetLength / (prevSpeed * m_maxLength);
            var duration = item.m_duration + offsetLength / prevMaxSpeed;

            if (!m_ignoreCost) {
                int ticketCost = netManager.m_lanes.m_buffer[item.m_laneID].m_ticketCost;
                if (ticketCost != 0) {
                    comparisonValue += ticketCost * m_pathRandomizer.Int32(2000u) * TICKET_COST_CONVERSION_FACTOR;
                }
            }

            if (nextLaneIndex < nextNumLanes) {
                var nextLaneInfo = nextSegmentInfo.m_lanes[nextLaneIndex];
                var nextItem = default(BufferItem);

                nextItem.m_position.m_segment = nextSegmentId;
                nextItem.m_position.m_lane = (byte)nextLaneIndex;
                nextItem.m_position.m_offset = offset;

                if ((nextLaneInfo.m_laneType & prevLaneType) == NetInfo.LaneType.None) {
                    nextItem.m_methodDistance = 0f;
                } else {
                    if (item.m_methodDistance == 0f) {
                        comparisonValue += 100f / (0.25f * m_maxLength);
                    }
                    nextItem.m_methodDistance = methodDistance + distance;
                }

                if (nextLaneInfo.m_laneType == NetInfo.LaneType.Pedestrian && !(nextItem.m_methodDistance < 1000f) && !m_stablePath) {
                    return;
                }

                nextItem.m_comparisonValue = comparisonValue + distance / ((prevMaxSpeed + nextLaneInfo.m_speedLimit) * 0.25f * m_maxLength);
                nextItem.m_duration = duration + distance / ((prevMaxSpeed + nextLaneInfo.m_speedLimit) * 0.5f);
                if ((nextSegment.m_flags & NetSegment.Flags.Invert) != NetSegment.Flags.None) {
                    nextItem.m_direction = NetInfo.InvertDirection(nextLaneInfo.m_finalDirection);
                } else {
                    nextItem.m_direction = nextLaneInfo.m_finalDirection;
                }

                if (nextLaneId == m_startLaneA) {
                    if ((nextItem.m_direction & NetInfo.Direction.Forward) == NetInfo.Direction.None || nextItem.m_position.m_offset < m_startOffsetA) {
                        if ((nextItem.m_direction & NetInfo.Direction.Backward) == NetInfo.Direction.None) {
                            return;
                        }
                        if (nextItem.m_position.m_offset > m_startOffsetA) {
                            return;
                        }
                    }
                    var nextSpeed = CalculateLaneSpeed(m_startOffsetA, nextItem.m_position.m_offset, ref nextSegment, nextLaneInfo);
                    var nextOffset = Mathf.Abs(nextItem.m_position.m_offset - m_startOffsetA) * BYTE_TO_FLOAT_OFFSET_CONVERSION_FACTOR;

                    nextItem.m_comparisonValue += nextOffset * nextSegment.m_averageLength / (nextSpeed * m_maxLength);
                    nextItem.m_duration += nextOffset * nextSegment.m_averageLength / nextSpeed;
                }

                if (nextLaneId == m_startLaneB) {
                    if ((nextItem.m_direction & NetInfo.Direction.Forward) == NetInfo.Direction.None || nextItem.m_position.m_offset < m_startOffsetB) {
                        if ((nextItem.m_direction & NetInfo.Direction.Backward) == NetInfo.Direction.None) {
                            return;
                        }
                        if (nextItem.m_position.m_offset > m_startOffsetB) {
                            return;
                        }
                    }
                    var nextSpeed = CalculateLaneSpeed(m_startOffsetB, nextItem.m_position.m_offset, ref nextSegment, nextLaneInfo);
                    var nextOffset = Mathf.Abs(nextItem.m_position.m_offset - m_startOffsetB) * BYTE_TO_FLOAT_OFFSET_CONVERSION_FACTOR;

                    nextItem.m_comparisonValue += nextOffset * nextSegment.m_averageLength / (nextSpeed * m_maxLength);
                    nextItem.m_duration += nextOffset * nextSegment.m_averageLength / nextSpeed;
                }

                nextItem.m_laneID = nextLaneId;
                nextItem.m_lanesUsed = item.m_lanesUsed | nextLaneInfo.m_laneType;

                AddBufferItem(nextItem, item.m_position);
            }
        }

        private void AddBufferItem(BufferItem item, PathUnit.Position target) {
            var laneLocation = m_laneLocation[item.m_laneID];
            var locPathFindIndex = laneLocation >> 16; // upper 16 bit, expected (?) path find index
            var bufferIndex = (int)(laneLocation & 65535u); // lower 16 bit
            int comparisonBufferPos;

            if (locPathFindIndex == m_pathFindIndex) {
                if (item.m_comparisonValue >= m_buffer[bufferIndex].m_comparisonValue) {
                    return;
                }

                var bufferPosIndex = bufferIndex >> 6; // arithmetic shift (sign stays), upper 10 bit
                var bufferPos = bufferIndex & -64; // upper 10 bit (no shift)
                if (bufferPosIndex < m_bufferMinPos || (bufferPosIndex == m_bufferMinPos && bufferPos < m_bufferMin[bufferPosIndex])) {
                    return;
                }

                comparisonBufferPos = Mathf.Max(Mathf.RoundToInt(item.m_comparisonValue * 1024f), m_bufferMinPos);
                if (comparisonBufferPos == bufferPosIndex) {
                    m_buffer[bufferIndex] = item;
                    m_laneTarget[item.m_laneID] = target;
                    return;
                }

                var newBufferIndex = bufferPosIndex << 6 | m_bufferMax[bufferPosIndex]--;
                var bufferItem = m_buffer[newBufferIndex];
                m_laneLocation[bufferItem.m_laneID] = laneLocation;
                m_buffer[bufferIndex] = bufferItem;
            } else {
                comparisonBufferPos = Mathf.Max(Mathf.RoundToInt(item.m_comparisonValue * 1024f), m_bufferMinPos);
            }

            if (comparisonBufferPos >= 1024 || comparisonBufferPos < 0) {
                return;
            }

            while (m_bufferMax[comparisonBufferPos] == 63) {
                comparisonBufferPos++;
                if (comparisonBufferPos == 1024) {
                    return;
                }
            }

            if (comparisonBufferPos > m_bufferMaxPos) {
                m_bufferMaxPos = comparisonBufferPos;
            }

            bufferIndex = comparisonBufferPos << 6 | ++m_bufferMax[comparisonBufferPos];
            m_buffer[bufferIndex] = item;
            m_laneLocation[item.m_laneID] = (uint)((int)(m_pathFindIndex << 16) | bufferIndex);
            m_laneTarget[item.m_laneID] = target;
        }

        private float CalculateLaneSpeed(byte startOffset, byte endOffset, ref NetSegment segment, NetInfo.Lane laneInfo) {
            var direction = (segment.m_flags & NetSegment.Flags.Invert) == NetSegment.Flags.None ? laneInfo.m_finalDirection : NetInfo.InvertDirection(laneInfo.m_finalDirection);
            if ((direction & NetInfo.Direction.Avoid) != 0) {
                if (endOffset > startOffset && direction == NetInfo.Direction.AvoidForward) {
                    return laneInfo.m_speedLimit * 0.1f;
                }

                if (endOffset < startOffset && direction == NetInfo.Direction.AvoidBackward) {
                    return laneInfo.m_speedLimit * 0.1f;
                }

                return laneInfo.m_speedLimit * 0.2f;
            }

            return laneInfo.m_speedLimit;
        }

        private void GetLaneDirection(PathUnit.Position pathPos, out NetInfo.Direction direction, out NetInfo.LaneType type) {
            var netManager = Singleton<NetManager>.instance;
            var info = netManager.m_segments.m_buffer[pathPos.m_segment].Info;
            if (info.m_lanes.Length > pathPos.m_lane) {
                direction = info.m_lanes[pathPos.m_lane].m_finalDirection;
                type = info.m_lanes[pathPos.m_lane].m_laneType;
                if ((netManager.m_segments.m_buffer[pathPos.m_segment].m_flags & NetSegment.Flags.Invert) != NetSegment.Flags.None) {
                    direction = NetInfo.InvertDirection(direction);
                }
            } else {
                direction = NetInfo.Direction.None;
                type = NetInfo.LaneType.None;
            }
        }

        private void PathFindThread() {
            while (true) {
                if (Monitor.TryEnter(m_queueLock, SimulationManager.SYNCHRONIZE_TIMEOUT)) {
                    try {
                        while (m_queueFirst == 0 && !m_terminated) {
                            Monitor.Wait(m_queueLock);
                        }

                        if (m_terminated) {
                            return;
                        }

                        m_calculating = m_queueFirst;
                        m_queueFirst = m_pathUnits.m_buffer[m_calculating].m_nextPathUnit;
                        if (m_queueFirst == 0) {
                            m_queueLast = 0u;
                            m_queuedPathFindCount = 0;
                        } else {
                            m_queuedPathFindCount--;
                        }

                        m_pathUnits.m_buffer[m_calculating].m_nextPathUnit = 0u;
                        m_pathUnits.m_buffer[m_calculating].m_pathFindFlags = (byte)((m_pathUnits.m_buffer[m_calculating].m_pathFindFlags & -2) | 2);
                        goto end_IL_001a;
                        end_IL_001a:;
                    } finally {
                        Monitor.Exit(m_queueLock);
                    }

                    try {
                        m_pathfindProfiler.BeginStep();
                        try {
                            PathFindImplementation(m_calculating, ref m_pathUnits.m_buffer[m_calculating]);
                        } finally {
                            m_pathfindProfiler.EndStep();
                        }
                    } catch (Exception ex) {
                        UIView.ForwardException(ex);
                        CODebugBase<LogChannel>.Error(LogChannel.Core, "Path find error: " + ex.Message + "\n" + ex.StackTrace);
                        m_pathUnits.m_buffer[m_calculating].m_pathFindFlags |= 8;
                    }

                    while (!Monitor.TryEnter(m_queueLock, SimulationManager.SYNCHRONIZE_TIMEOUT)) {
                    }

                    try {
                        m_pathUnits.m_buffer[m_calculating].m_pathFindFlags = (byte)(m_pathUnits.m_buffer[m_calculating].m_pathFindFlags & -3);
                        Singleton<PathManager>.instance.ReleasePath(m_calculating);
                        m_calculating = 0u;
                        Monitor.Pulse(m_queueLock);
                    } finally {
                        Monitor.Exit(m_queueLock);
                    }
                }
            }
        }
    }
}