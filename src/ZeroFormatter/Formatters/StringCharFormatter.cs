﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ZeroFormatter.Internal;

namespace ZeroFormatter.Formatters
{
    // Layout: [size:int][utf8bytes...], if size == -1 string is null.
    internal class StringFormatter : Formatter<string>
    {
        public override int? GetLength()
        {
            return null;
        }

        public override int Serialize(ref byte[] bytes, int offset, string value)
        {
            if (value == null)
            {
                BinaryUtil.WriteInt32(ref bytes, offset, -1);
                return 4;
            }

            var stringSize = BinaryUtil.WriteString(ref bytes, offset + 4, value);
            BinaryUtil.WriteInt32(ref bytes, offset, stringSize); // write size after encode.
            return stringSize + 4;
        }

        public override string Deserialize(ref byte[] bytes, int offset, out int byteSize)
        {
            var length = BinaryUtil.ReadInt32(ref bytes, offset);
            if (length == -1)
            {
                byteSize = 4;
                return null;
            }
            else
            {
                byteSize = length + 4;
            }

            return BinaryUtil.ReadString(ref bytes, offset + 4, length);
        }
    }

    // Layout: [size:int][utf8bytes...]
    internal class CharFormatter : Formatter<char>
    {
        public override int? GetLength()
        {
            return null;
        }

        public override int Serialize(ref byte[] bytes, int offset, char value)
        {
            var charSize = BinaryUtil.WriteChar(ref bytes, offset + 4, value);
            BinaryUtil.WriteInt32(ref bytes, offset, charSize); // write size after encode.
            return charSize + 4;
        }

        public override char Deserialize(ref byte[] bytes, int offset, out int byteSize)
        {
            var length = BinaryUtil.ReadInt32(ref bytes, offset);
            byteSize = length;
            return BinaryUtil.ReadChar(ref bytes, offset + 4, length);
        }
    }

    // Layout: [size:int][utf8bytes...], if size == -1 char is null.
    internal class NullableCharFormatter : Formatter<char?>
    {
        public override int? GetLength()
        {
            return null;
        }

        public override int Serialize(ref byte[] bytes, int offset, char? value)
        {
            if (value == null)
            {
                BinaryUtil.WriteInt32(ref bytes, offset, -1);
                return 4;
            }

            var charSize = BinaryUtil.WriteChar(ref bytes, offset + 4, value.Value);
            BinaryUtil.WriteInt32(ref bytes, offset, charSize); // write size after encode.
            return charSize + 4;
        }

        public override char? Deserialize(ref byte[] bytes, int offset, out int byteSize)
        {
            var length = BinaryUtil.ReadInt32(ref bytes, offset);
            if (length == -1)
            {
                byteSize = 4;
                return null;
            }
            else
            {
                byteSize = length + 4;
            }


            return BinaryUtil.ReadChar(ref bytes, offset + 4, length);
        }
    }
}