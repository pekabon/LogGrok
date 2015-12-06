using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace LogGrok.Unsafe
{
	public static class ByteArrayTools
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static unsafe bool FastEquals(byte[] left, int leftStart, byte[] right, int rightStart, int length)
		{
			var loopCount = length / 8;
			var restBytes = length % 8;

			fixed (byte* firstByteLeft = &(left[leftStart]))
			fixed (byte* firstByteRight = &(right[rightStart]))
			{
				var leftData = (long*)firstByteLeft;
				var rightData = (long*)firstByteRight;
				while (loopCount > 0)
				{
					if (*leftData != *rightData)
						return false;
					loopCount--;
					leftData++;
					rightData++;
				}


				if (restBytes >= 4)
				{
					var leftDataInt = (int*)leftData;
					var rightDataInt = (int*)rightData;
					if (*rightDataInt != *leftDataInt)
					{
						return false;
					}
					else
					{
						restBytes -= 4;
						leftData = (long*)(++leftDataInt);
						rightData = (long*)(++rightDataInt);
					}
				}

				if (restBytes >= 2)
				{
					var leftDataShort = (short*)leftData;
					var rightDataShort = (short*)rightData;
					if (*rightDataShort != *leftDataShort)
					{
						return false;
					}
					else
					{
						restBytes -= 2;
						leftData = (long*)(++leftDataShort);
						rightData = (long*)(++rightDataShort);
					}
				}

				if (restBytes > 0)
				{
					return *((byte*)leftData) == *((byte*)rightData);
				}

				return true;
			}
		}
	}
}
