﻿using System;
using System.Reflection;

namespace NetRt.Metadata.TableElements
{
    public readonly struct Field
    {
        public Field(FieldAttributes flags, string name, uint signature)
        {
            Flags = flags;
            Name = name;
            Signature = signature;
        }

        public FieldAttributes Flags { get; }
        public string Name { get; }
        public uint Signature { get; }

        public FieldSig ReadSignature()
        {
            throw new NotImplementedException();
        }
    }
}