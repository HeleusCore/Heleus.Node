using System;
namespace Heleus.Base
{
    public static class Mth
    {
        public static float Percentage(int value, int total)
        {
            if (total <= 0)
                return 0;

            return ((100f * value) / total);
        }
    }
}
