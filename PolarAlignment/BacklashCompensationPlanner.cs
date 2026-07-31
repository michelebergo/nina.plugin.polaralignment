using System;

namespace NINA.Plugins.PolarAlignment {
    internal static class BacklashCompensationPlanner {
        public static (float FirstMove, float SecondMove) CreateSequence(float compensation, LastDirection targetDirection) {
            var directionSign = targetDirection == LastDirection.Positive ? 1f : -1f;
            return (-directionSign * compensation, directionSign * compensation);
        }
    }
}
