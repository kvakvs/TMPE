namespace TrafficManager.UI.SubTools.SpeedLimits {
    using GenericGameBridge.Service;
    using TrafficManager.API.Traffic.Data;
    using TrafficManager.Manager.Impl;
    using TrafficManager.State;
    using TrafficManager.U;
    using TrafficManager.UI.SubTools.PrioritySigns;
    using TrafficManager.Util;
    using UnityEngine;

    /// <summary>
    /// Implements new style Speed Limits palette and speed limits management UI.
    /// </summary>
    public class SpeedLimitsTool
        : TrafficManagerSubTool,
          UI.MainMenu.IOnscreenDisplayProvider
    {
        public const ushort LOWER_KMPH = 10;
        public const ushort UPPER_KMPH = 140;
        public const ushort KMPH_STEP = 10;

        public const ushort LOWER_MPH = 5;
        public const ushort UPPER_MPH = 90;
        public const ushort MPH_STEP = 5;

        /// <summary>
        /// Currently selected speed limit on the limits palette.
        /// units less than 0: invalid (not selected)
        /// units = 0: no limit.
        /// </summary>
        public SpeedValue CurrentPaletteSpeedLimit = new SpeedValue(-1f);

        /// <summary>
        /// Will show and edit speed limits for each lane.
        /// This is toggled by the tool window button or by holding Ctrl temporarily.
        /// </summary>
        private bool showLimitsPerLane_;

        private bool ShowLimitsPerLane => showLimitsPerLane_ ^ Shortcuts.ControlIsPressed;

        /// <summary>
        /// Will edit entire road between two junctions.
        /// This is toggled by holding Shift.
        /// </summary>
        private bool multiSegmentMode_;
        private bool MultiSegmentMode => multiSegmentMode_ ^ Shortcuts.ShiftIsPressed;

        /// <summary>
        /// Finite State machine for the tool. Represents current UI state for Lane Arrows.
        /// </summary>
        private Util.GenericFsm<State, Trigger> fsm_;

        private SpeedLimitsOverlay.DrawArgs overlayDrawArgs_;
        private SpeedLimitsOverlay overlay_;

        /// <summary>Tool states.</summary>
        private enum State {
            /// <summary>Clicking a segment will override speed limit on all lanes.
            /// Holding Alt will temporarily show the Defaults.
            /// </summary>
            EditSegments,

            /// <summary>Clicking a road type will override default.</summary>
            EditDefaults,

            /// <summary>The user requested to leave the tool.</summary>
            ToolDisabled,
        }

        /// <summary>Events which trigger state transitions.</summary>
        private enum Trigger {
            /// <summary>Mode 1 - Segment Edit Mode - clicked.</summary>
            SegmentsButtonClick,

            /// <summary>Mode 2 - Edit Defaults - clicked.</summary>
            DefaultsButtonClick,

            /// <summary>Right mouse has been clicked.</summary>
            RightMouseClick,
        }

        /// <summary>If exists, contains tool panel floating on the selected node.</summary>
        private SpeedLimitsWindow Window { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="SpeedLimitsTool"/> class.
        /// </summary>
        /// <param name="mainTool">Reference to the parent maintool.</param>
        public SpeedLimitsTool(TrafficManagerTool mainTool)
            : base(mainTool) {
            overlay_ = new SpeedLimitsOverlay(mainTool: this.MainTool);
        }

        /// <summary>
        /// Creates FSM ready to begin editing. Or recreates it when ESC is pressed
        /// and the tool is canceled.
        /// </summary>
        /// <returns>The new FSM in the initial state.</returns>
        private Util.GenericFsm<State, Trigger> InitFiniteStateMachine() {
            var fsm = new Util.GenericFsm<State, Trigger>(State.EditSegments);

            fsm.Configure(State.EditSegments)
               // .OnEntry(this.OnEnterSelectState)
               // .OnLeave(this.OnLeaveSelectState)
               .TransitionOnEvent(Trigger.DefaultsButtonClick, State.EditDefaults)
               .TransitionOnEvent(Trigger.RightMouseClick, State.ToolDisabled);

            fsm.Configure(State.EditDefaults)
               .TransitionOnEvent(Trigger.SegmentsButtonClick, State.EditSegments)
               .TransitionOnEvent(Trigger.RightMouseClick, State.ToolDisabled);

            fsm.Configure(State.ToolDisabled)
               .OnEntry(
                   () => {
                       // We are done here, leave the tool.
                       // This will result in this.DeactivateTool being called.
                       ModUI.Instance.MainMenu.ClickToolButton(ToolMode.LaneArrows);
                   });

            return fsm;
        }

        private static string T(string key) => Translation.SpeedLimits.Get(key);

        public override void ActivateTool() {
            // Create a generic self-sizing window with padding of 4px.
            void SetupFn(UiBuilder<SpeedLimitsWindow> b) {
                b.SetPadding(UConst.UIPADDING);
                b.Control.SetupControls(builder: b, parentTool: this);
            }
            this.Window = UiBuilder<SpeedLimitsWindow>.CreateWindow<SpeedLimitsWindow>(setupFn: SetupFn);
            this.fsm_ = InitFiniteStateMachine();
        }

        public override void DeactivateTool() {
            Object.Destroy(this.Window);
            this.Window = null;
            this.fsm_ = null;
        }

        public override void RenderActiveToolOverlay(RenderManager.CameraInfo cameraInfo) {
            CreateOverlayDrawArgs();
            overlay_.Render(cameraInfo: cameraInfo, args: this.overlayDrawArgs_);
            overlay_.ShowSigns(cameraInfo: cameraInfo, args: this.overlayDrawArgs_);
        }

        /// <summary>Copies important values for rendering the overlay into its args struct.</summary>
        private void CreateOverlayDrawArgs() {
            overlayDrawArgs_.InteractiveSigns = true;
            overlayDrawArgs_.MultiSegmentMode = this.MultiSegmentMode;
            overlayDrawArgs_.ShowLimitsPerLane = this.ShowLimitsPerLane;
            overlayDrawArgs_.ParentTool = this;
        }

        /// <summary>Render overlay for other tool modes, if speed limits overlay is on.</summary>
        /// <param name="cameraInfo">The camera.</param>
        public override void RenderGenericInfoOverlay(RenderManager.CameraInfo cameraInfo) {
            if (!Options.speedLimitsOverlay && !MassEditOverlay.IsActive) {
                return;
            }

            CreateOverlayDrawArgs();
            overlay_.ShowSigns(cameraInfo: cameraInfo, args: this.overlayDrawArgs_);
        }

        public override void OnToolLeftClick() {
        }

        public override void OnToolRightClick() {
        }

        public override void UpdateEveryFrame() {
        }

        /// <summary>Called when the tool must update onscreen keyboard/mouse hints.</summary>
        public void UpdateOnscreenDisplayPanel() {
        }

        // TODO: Possibly this is useful in more than this tool, then move it up the class hierarchy
        public bool ContainsMouse() {
            return Window != null && Window.containsMouse;
        }

        internal static void SetSpeedLimit(LanePos lane, SpeedValue? speed) {
            ushort segmentId = lane.laneId.ToLane().m_segment;
            SpeedLimitManager.Instance.SetSpeedLimit(
                segmentId: segmentId,
                laneIndex: lane.laneIndex,
                laneInfo: segmentId.ToSegment().Info.m_lanes[lane.laneIndex],
                laneId: lane.laneId,
                speedLimit: speed?.GameUnits);
        }
    } // end class
}
