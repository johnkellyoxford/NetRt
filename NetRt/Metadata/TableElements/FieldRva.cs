﻿using System;

namespace NetRt.Metadata.TableElements
{
    using Rva = UInt32;

    public readonly struct FieldRva
    {
        public FieldRva(uint rva, Field field)
        {
            Rva = rva;
            Field = field;
        }

        public Rva Rva { get; }
        public Field Field { get; }
    }
}