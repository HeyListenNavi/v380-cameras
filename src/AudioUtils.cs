using System;

namespace V380Decoder.src
{
    public class AudioUtils
    {
        private static readonly int[] T_INDEX = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };
        private static readonly int[] T_STEP = {
            7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
            50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230,
            253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963,
            1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327,
            3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487,
            12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
        };

        public class ImaAdpcmEncoder
        {
            private int _encoderPredicted = 0;
            private int _encoderIndex = 0;

            private byte EncodeSample(short sample)
            {
                int delta = sample - _encoderPredicted;
                byte value = 0;
                if (delta < 0)
                {
                    value = 8;
                    delta = -delta;
                }

                int step = T_STEP[_encoderIndex];
                int diff = step >> 3;

                if (delta > step)
                {
                    value |= 4;
                    delta -= step;
                    diff += step;
                }
                step >>= 1;

                if (delta > step)
                {
                    value |= 2;
                    delta -= step;
                    diff += step;
                }
                step >>= 1;

                if (delta > step)
                {
                    value |= 1;
                    diff += step;
                }

                if ((value & 8) != 0)
                {
                    _encoderPredicted -= diff;
                }
                else
                {
                    _encoderPredicted += diff;
                }

                if (_encoderPredicted < -0x8000) _encoderPredicted = -0x8000;
                else if (_encoderPredicted > 0x7FFF) _encoderPredicted = 0x7FFF;

                _encoderIndex += T_INDEX[value & 7];
                if (_encoderIndex < 0) _encoderIndex = 0;
                else if (_encoderIndex > 88) _encoderIndex = 88;

                return value;
            }

            public byte[] EncodeBlock(short[] samples)
            {
                if (samples.Length != 505)
                {
                    throw new ArgumentException("Samples length must be exactly 505.");
                }

                byte[] result = new byte[256];
                
                // Encode first sample to update state and write header
                EncodeSample(samples[0]);
                
                // Write header: first sample (16-bit), encoder index (8-bit), zero (8-bit)
                result[0] = (byte)(samples[0] & 0xFF);
                result[1] = (byte)((samples[0] >> 8) & 0xFF);
                result[2] = (byte)_encoderIndex;
                result[3] = 0x00;

                for (int n = 1; n <= 252; n++)
                {
                    byte sample2 = EncodeSample(samples[2 * n - 1]);
                    byte sample1 = EncodeSample(samples[2 * n]);
                    result[3 + n] = (byte)((sample1 << 4) | sample2);
                }

                return result;
            }
        }

        public static short ALawToLinear(byte alaw)
        {
            alaw ^= 0xD5;
            int sign = alaw & 0x80;
            int exponent = (alaw & 0x70) >> 4;
            int mantissa = alaw & 0x0F;
            int sample = (mantissa << 4) + 8;
            if (exponent > 0)
            {
                sample = (sample + 0x100) << (exponent - 1);
            }
            return (short)(sign == 0 ? sample : -sample);
        }

        public static short[] ALawToLinear(byte[] alawData)
        {
            short[] linear = new short[alawData.Length];
            for (int i = 0; i < alawData.Length; i++)
            {
                linear[i] = ALawToLinear(alawData[i]);
            }
            return linear;
        }
    }
}
