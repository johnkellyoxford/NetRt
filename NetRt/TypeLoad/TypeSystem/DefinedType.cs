﻿using NetRt.Assemblies;
using NetRt.Metadata.TableElements;

namespace NetRt.TypeLoad.TypeSystem
{
    public class DefinedType : TypeInformation
    {
        public override int Size { get; }
        public override Field[] Fields { get; }
        public override MethodDef[] Methods { get; }

        public override bool IsObject => true;

        public DefinedType(uint token) : base(token)
        {
        }
    }
}