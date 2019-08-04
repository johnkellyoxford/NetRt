﻿using NetInterface;

namespace NetRt.TypeLoad.TypeSystem
{
    public abstract class TypeDefinition
    {
        public abstract Field[] Fields { get; }
        public abstract MethodDef[] Methods { get; }

        public abstract bool IsObject { get; } // false if ByRef/Pointer/not derived from object
    }
}