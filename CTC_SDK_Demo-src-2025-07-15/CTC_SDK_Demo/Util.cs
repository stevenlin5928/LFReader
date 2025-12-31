using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CommandDemo
{
    class Util
    {
        /// <summary>
        /// 异或运算
        /// </summary>
        /// <param name="data"></param>
        /// <param name="start_index"></param>
        /// <param name="len"></param>
        /// <returns></returns>
        public static byte Xor(byte[] data, int start_index, int len)
        {
            byte myxor = 0;
            for (int i = start_index; i < start_index + len; i++)
                myxor ^= data[i];
            return myxor;
        }
        /// <summary>
        /// 生成CRC校验值
        /// </summary>
        /// <param name="val"></param>
        /// <returns></returns>
        public static byte Checksum(params byte[] val)
        {
            if (val == null)
                throw new ArgumentNullException("val");

            byte c = 0;

            foreach (byte b in val)
            {
                c = (byte)(c ^ b);
            }

            return c;
        }
        /*******************************************************************
            * * 函数名称：ToHexString
            * * 功    能：获取字节数组的16进制
            * * 参    数：bytes 字节数组
            * * 返 回 值：
            * 
            * *******************************************************************/
        public static string ToHexString(byte[] bytes)
        {
            if (bytes != null)
            {
                char[] chars = new char[bytes.Length * 2];
                for (int i = 0; i < bytes.Length; i++)
                {
                    int b = bytes[i];
                    chars[i * 2] = hexDigits[b >> 4];
                    chars[i * 2 + 1] = hexDigits[b & 0xF];
                }
                return new string(chars);
            }
            else
                return null;
        }

        static char[] hexDigits = {
        '0', '1', '2', '3', '4', '5', '6', '7',
        '8', '9', 'A', 'B', 'C', 'D', 'E', 'F'};

        /*******************************************************************
       * * 函数名称：ToHexByte
       * * 功    能：获取16进制字符串的字节数组
       * * 参    数：hexString 16进制字符串
       * * 返 回 值：
       * 
       * *******************************************************************/
        public static byte[] ToHexByte(string hexString)
        {
            hexString = hexString.Replace(" ", "");
            if ((hexString.Length % 2) != 0)
                hexString += " ";
            byte[] returnBytes = new byte[hexString.Length / 2];
            for (int i = 0; i < returnBytes.Length; i++)
                returnBytes[i] = Convert.ToByte(hexString.Substring(i * 2, 2), 16);
            return returnBytes;
        }



        /// <summary>
        /// 返回两个时间的毫秒数
        /// </summary>
        /// <param name="endTime">结束时间</param>
        /// <param name="startTime">开始时间</param>
        /// <returns></returns>
        public static double DateDiffMillSecond(DateTime endTime, DateTime startTime)
        {
            double dateDiff;
            TimeSpan ts1 = new TimeSpan(endTime.Ticks);
            TimeSpan ts2 = new TimeSpan(startTime.Ticks);
            TimeSpan ts = ts1.Subtract(ts2).Duration();

            dateDiff = ts.TotalMilliseconds;
            return dateDiff;
        }

        public const string PATTERN = @"([^A-Fa-f0-9]|\s+?)+";
        /// <summary>
        /// 判断十六进制字符串hex是否正确
        /// </summary>
        /// <param name="hex">十六进制字符串</param>
        /// <returns>true：不正确，false：正确</returns>
        public static bool IsIllegalHexadecimal(string hex)
        {
            return System.Text.RegularExpressions.Regex.IsMatch(hex, PATTERN);
        }
    }
}
