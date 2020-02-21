using System;
namespace System
{
    public static class ByteExtension
    {
        public static bool Equals(this byte[] data, params byte[] values)
        {
            if (data == null || values == null || data.Length != values.Length)
                return false;

            for (var i = 0; i < data.Length; i++)
            {
                if (data[i] != values[i])
                    return false;
            }
            return true;
        }
    }
}
