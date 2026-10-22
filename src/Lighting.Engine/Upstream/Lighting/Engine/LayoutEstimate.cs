using System;

namespace WWTools.Lighting.Engine
{
    /// <summary>
    /// The lumen-method guess that seeds a layout, and the scaling that fits one to a target.
    /// Only a starting point: the solver gives the real numbers.
    /// </summary>
    public static class LayoutEstimate
    {
        /// <summary>Coefficient of utilisation assumed for the first guess.</summary>
        public const double AssumedCu = 0.6;

        /// <summary>Fixtures for an average of <paramref name="targetFc"/> over <paramref name="areaSqFt"/>; at least 1.</summary>
        public static int CountFor(double targetFc, double areaSqFt, double lumensPerFixture, double lightLossFactor)
        {
            if (lumensPerFixture <= 0 || lightLossFactor <= 0 || targetFc <= 0 || areaSqFt <= 0) return 1;
            return Math.Max(1, (int)Math.Ceiling(targetFc * areaSqFt / (lumensPerFixture * lightLossFactor * AssumedCu)));
        }

        /// <summary>The count that would move <paramref name="achievedFc"/> to the target; unchanged when nothing was achieved.</summary>
        public static int Scaled(int current, double achievedFc, double targetFc)
        {
            if (achievedFc <= 0) return current;
            return Math.Max(1, (int)Math.Ceiling(current * targetFc / achievedFc));
        }

        /// <summary>At or above the target, and no more than 5 % over it.</summary>
        public static bool CloseEnough(double achievedFc, double targetFc) =>
            achievedFc >= targetFc && achievedFc <= targetFc * 1.05;
    }
}
