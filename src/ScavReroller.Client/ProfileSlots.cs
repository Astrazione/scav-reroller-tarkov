using System;

namespace ScavReroller.Client
{
    internal static class ProfileSlots
    {
        internal static T[] ReplaceSingleReference<T>(T[] profiles, T previous, T replacement) where T : class
        {
            var result = (T[])profiles.Clone();
            var slot = -1;
            for (var index = 0; index < result.Length; index++)
            {
                if (!ReferenceEquals(result[index], previous)) continue;
                if (slot >= 0) throw new InvalidOperationException("Duplicate scav slots in the session.");
                slot = index;
            }
            if (slot < 0) throw new InvalidOperationException("Current scav slot was not found in the session.");
            result[slot] = replacement;
            return result;
        }
    }
}
