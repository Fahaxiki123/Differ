
/* ====================================================================
   Licensed to the Apache Software Foundation (ASF) under one or more
   contributor license agreements.  See the NOTICE file distributed with
   this work for Additional information regarding copyright ownership.
   The ASF licenses this file to You under the Apache License, Version 2.0
   (the "License"); you may not use this file except in compliance with
   the License.  You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
==================================================================== */

namespace NPOI.HWPF.Model
{
    using System;
    using System.IO;
    using NPOI.Util;

    // Differ deviation: implements the released NPOI 2.7.4 interface name
    // (ILittleEndianInput in NPOI.Util) instead of the fossilized
    // LittleEndianInput from NPOI.Util.IO, which no longer exists.
    public class HWPFStream : ILittleEndianInput
    {
        private ILittleEndianInput _le;
        private int _currentDataOffset = 0;

        public HWPFStream(Stream stream)
        {
            _le = new LittleEndianInputStream(stream);
        }

        #region LittleEndianInput Members

        public int Available()
        {
            throw new NotImplementedException();
        }

        public int ReadByte()
        {
            _currentDataOffset += LittleEndianConsts.BYTE_SIZE;
            return _le.ReadByte();
        }

        public int ReadUByte()
        {
            int s = ReadByte();
            if (s < 0)
            {
                s += 256;
            }
            return s;
        }

        public short ReadShort()
        {
            _currentDataOffset += LittleEndianConsts.SHORT_SIZE;
            return _le.ReadShort();
        }

        public int ReadUShort()
        {
            _currentDataOffset += LittleEndianConsts.SHORT_SIZE;
            return _le.ReadUShort();
        }

        public int ReadInt()
        {
            _currentDataOffset += LittleEndianConsts.INT_SIZE;
            return _le.ReadInt();
        }

        public long ReadLong()
        {
            _currentDataOffset += LittleEndianConsts.LONG_SIZE;
            return _le.ReadLong();
        }

        public double ReadDouble()
        {
            _currentDataOffset += LittleEndianConsts.DOUBLE_SIZE;

            long valueLongBits = _le.ReadLong();
            double result = BitConverter.Int64BitsToDouble(valueLongBits);
            return result;
        }

        public void ReadFully(byte[] buf)
        {
            ReadFully(buf, 0, buf.Length);
        }

        public void ReadFully(byte[] buf, int off, int len)
        {
            _le.ReadFully(buf, off, len);
            _currentDataOffset += len;
        }

        #endregion
    }
}
