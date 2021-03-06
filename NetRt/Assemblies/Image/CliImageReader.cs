﻿using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using NetRt.Assemblies.Heaps;
using NetRt.Common;

#nullable enable

namespace NetRt.Assemblies.Image
{
    using Rva = UInt32;
    using CodedIndex = TableHeap.CodedIndex;

    public sealed partial class CliImage
    {
        public class CliImageReader
        {
            public CliImage Image { get; }
            private readonly Disposable<Stream> _disposableStream;
            private Stream Stream => _disposableStream.Value;

            public CliImageReader(Disposable<Stream> imageStream, string name)
            {
                _disposableStream = imageStream;
                Image = new CliImage(name);


                const int lfanewIndex = 0x3c;

                Stream stream = imageStream.Value;

                uint lfanew = 0;
#if VALIDATE_FULL_DOSHEADER // Do we need to do this?

                // This is embedded in the file metadata and is zero-cost to initialize
                ReadOnlySpan<byte> dosHeader = new byte[]
                {
                    0x4d, 0x5a, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00,
                    0x04, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00,
                    0xb8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                    0x40, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                    0x00, 0x00, 0x00, 0x00, /* begin lfanew */ 0xDE, 0xAD, 0xBE, 0xEF, /* end lfanew */
                    0x0e, 0x1f, 0xba, 0x0e, 0x00, 0xb4, 0x09, 0xcd,
                    0x21, 0xb8, 0x01, 0x4c, 0xcd, 0x21, 0x54, 0x68,
                    0x69, 0x73, 0x20, 0x70, 0x72, 0x6f, 0x67, 0x72,
                    0x61, 0x6d, 0x20, 0x63, 0x61, 0x6e, 0x6e, 0x6f,
                    0x74, 0x20, 0x62, 0x65, 0x20, 0x72, 0x75, 0x6e,
                    0x20, 0x69, 0x6e, 0x20, 0x44, 0x4f, 0x53, 0x20,
                    0x6d, 0x6f, 0x64, 0x65, 0x2e, 0x0d, 0x0d, 0x0a,
                    0x24, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
                };

                for (var i = 0; i < dosHeader.Length; i++)
                {
                    if (i == lfanewIndex)
                    {
                        lfanew = _stream.Read<uint>();

                        i += 3;
                        continue;
                    }

                    if (stream.ReadByte() != dosHeader[i])
                        ThrowHelper.ThrowBadImageFormatException(CliResources.ResourceManager.GetString("InvalidMsDosHeader"));
                }
#else
                stream.Position = lfanewIndex;
                lfanew = Stream.Read<uint>();
#endif // VALIDATE_FULL_DOSHEADER

                stream.Seek(lfanew, SeekOrigin.Begin); // PE Header


                // Verify PE Header string is (ascii) "PE\0\0"
                // Always little endian
                if (stream.Read<int>() != /* "PE\0\0" */ 0x00_00_45_50)
                {
                    ThrowHelper.ThrowBadImageFormatException(NetRtResources.GetResource("InvalidPeHeaderString"));
                }

                // Machine - always 0x14c
                if (stream.Read<ushort>() != 0x14c)
                    ThrowHelper.ThrowBadImageFormatException(NetRtResources.GetResource("InvalidMachine"));

                Image.NumSections = stream.Read<ushort>();

                Image.TimeStamp = DateTimeOffset.FromUnixTimeSeconds(stream.Read<uint>()).DateTime;

                // Skip the 4-byte pointer to symbol table, which is ignored
                // Skip the 4-byte number of symbols, which is ignored
                // Skip the 2-byte optional header size, which we don't need
                stream.Skip(10);

                Image.Characteristics = stream.Read<Characteristics>();

                var magic = stream.Read<ushort>();

                bool bit64 = magic == 0x20b;

                // Skip all the way to 'Reserved'
                stream.Skip(50);

                if (stream.Read<int>() != 0)
                    ThrowHelper.ThrowBadImageFormatException(NetRtResources.GetResource("InvalidHeaderValue"));

                // Skip to the subsystem
                stream.Skip(12);

                Image.Subsystem = stream.Read<Subsystem>();

                Image.DllFlags = stream.Read<DllFlags>();

                // Skip past the stack details and go to the tables
                stream.Skip(bit64 ? 64 : 48);

                Image.BaseRelocationTable = stream.ReadDataDirectory();

                stream.Skip(80);

                Image.Cli = stream.ReadDataDirectory();

                stream.Skip(8);

                ReadSections();
                ReadCliHeader();
                ReadMetadata();
                // TODO ReadDebugHeader();

                Image.ModuleType = GetModuleType(Image.Characteristics, Image.Subsystem);
            }

            private static ModuleType GetModuleType(Characteristics characteristics, Subsystem subsystem)
            {
                if (characteristics.HasFlag(Characteristics.IMAGE_FILE_DLL))
                    return ModuleType.Dll;

                if (subsystem == Subsystem.IMAGE_SUBSYSTEM_WINDOWS_GUI || subsystem == Subsystem.IMAGE_SUBSYSTEM_WINDOWS_CE_GUI)
                    return ModuleType.Windows;

                return ModuleType.Console;
            }

            private unsafe void ReadSections()
            {
                var sections = new Section[Image.NumSections];

                for (var i = 0; i < sections.Length; i++)
                {
                    var section = new Section();
                    Span<byte> name = stackalloc byte[8];
                    Stream.Read(name);

                    section.Name = Encoding.ASCII.GetString(name);
                    section.VirtualSize = Stream.Read<uint>();
                    section.VirtualAddress = Stream.Read<uint>();
                    section.SizeOfRawData = Stream.Read<uint>();
                    section.PointerToRawData = Stream.Read<uint>();

                    Stream.Skip(12);

                    section.Characteristics = Stream.Read<Section.SectionCharacteristics>();

                    sections[i] = section;
                }

                Image.Sections = sections;
            }

            public uint RvaToFileAddress(Rva rva)
            {
                Section section = GetSectionAtVirtualAddress(rva);

                return (rva + section.PointerToRawData) - section.VirtualAddress;
            }

            private Section GetSectionAtVirtualAddress(Rva rva)
            {
                Section[] sections = Image.Sections;
                foreach (Section section in sections)
                {
                    if (rva >= section.VirtualAddress && rva < section.VirtualAddress + section.SizeOfRawData)
                        return section;
                }

                throw new ArgumentOutOfRangeException(nameof(rva));
            }

            private void GotoRva(Rva rva)
            {
                Stream.Goto(RvaToFileAddress(rva));
            }

            private void ReadCliHeader()
            {
                GotoRva(Image.Cli.Rva);

                Stream.Skip(4);
                Image.RuntimeVersion = new Version(Stream.Read<ushort>(), Stream.Read<ushort>());

                Image.Metadata = Stream.ReadDataDirectory();
                Image.Flags = Stream.Read<RuntimeFlags>();
                Image.EntryPointToken = Stream.Read<uint>();
                Image.Resources = Stream.ReadDataDirectory();
                Image.StrongNameSignature = Stream.ReadDataDirectory();
            }

            private unsafe void ReadMetadata()
            {
                GotoRva(Image.Metadata.Rva);

                if (Stream.Read<uint>() != /* Magic num */ 0x424A5342)
                    ThrowHelper.ThrowBadImageFormatException(NetRtResources.GetResource("InvalidMetadata"));

                Stream.Skip(8);

                var len = Stream.Read<uint>();
                Span<byte> versionStr = stackalloc byte[(int)len];
                Stream.Read(versionStr);

                Image.MetadataVersion = Encoding.ASCII.GetString(versionStr);

                Stream.Skip(2);

                var numStreams = Stream.Read<ushort>();

                Section metadataSection = GetSectionAtVirtualAddress(Image.Metadata.Rva);

                if (metadataSection is null)
                    ThrowHelper.ThrowBadImageFormatException(NetRtResources.GetResource("NoMetadataSection"));

                Image.MetadataSection = metadataSection!;

                uint tableHeapOffset = 0;
                for (var i = 0; i < numStreams; i++)
                {
                    uint offset = Image.Metadata.Rva - metadataSection!.VirtualAddress + Stream.Read<uint>();

                    var size = Stream.Read<uint>();

                    long pos = Stream.Position;

                    Stream.Goto(offset + Image.MetadataSection.PointerToRawData);
                    var data = new byte[size];
                    Stream.Read(data);
                    Stream.Goto(pos);



                    string name = ReadStreamHeaderName();

                    switch (name)
                    {
                        case "#~":
                        case "#-":
                            Image.TableHeap = new TableHeap(data);
                            tableHeapOffset = offset;
                            break;
                        case "#Strings":
                            Image.StringHeap = new StringHeap(data);
                            break;
                        case "#Blob":
                            Image.BlobHeap = new BlobHeap(data);
                            break;
                        case "#GUID":
                            Image.GuidHeap = new GuidHeap(data);
                            break;
                        case "#US":
                            Image.UserStringHeap = new UserStringHeap(data);
                            break;
                        case "#Pdb":
                            Debug.WriteLine("#Pdb tables not yet supported");
                            break;
                    }
                }

                Image.TableHeapOffset = tableHeapOffset;

                if (Image.TableHeap is object)
                {
                    Stream.Goto(tableHeapOffset + Image.MetadataSection.PointerToRawData);

                    // Skip reserved (4 bytes), and the version (2 bytes)
                    Stream.Skip(6);

                    var size = Stream.Read<byte>();

                    // Skip reserved
                    Stream.Skip(1);

                    TableHeap heap = Image.TableHeap;
                    heap.ValidTables = Stream.Read<long>();
                    heap.Sorted = Stream.Read<long>();

                    for (var i = 0; i < TableHeap.TableCount; i++)
                    {
                        if (!heap.HasTable((TableHeap.Table)i)) continue;

                        heap.Tables[i].Length = Stream.Read<uint>();
                    }

                    SetIndexSize(Image.StringHeap, 1);
                    SetIndexSize(Image.GuidHeap, 2);
                    SetIndexSize(Image.BlobHeap, 4);

                    ComputeTableInformation(tableHeapOffset);

                    void SetIndexSize(Heap heap, byte flag)
                    {
                        heap.IndexSize = (size & flag) > 0 ? 4 : 2;
                    }
                }
            }

            private string ReadStreamHeaderName()
            {
                Span<char> res = stackalloc char[32];
                var len = 32;

                for (var i = 0; i < res.Length; i++)
                {
                    var b = (byte)Stream.ReadByte();
                    if (b == 0)
                    {
                        len = i;
                        break;
                    }

                    res[i] = (char)b;
                }

                AlignStream();
                return res.Slice(0, len).ToString();

                void AlignStream()
                {
                    var pos = (ulong)Stream.Position;
                    Stream.Goto((int)((pos + 3UL) & ~3UL));
                }
            }

            private void ComputeTableInformation(uint tableHeapOffset)
            {
                uint offset =
                    (uint)Stream.Position - tableHeapOffset - Image.MetadataSection.PointerToRawData; // header

                int stridxSize = Image.StringHeap.IndexSize;
                int guididxSize = Image.GuidHeap?.IndexSize ?? 2;
                int blobidxSize = Image.BlobHeap?.IndexSize ?? 2;

                var heap = Image.TableHeap;
                var tables = heap.Tables;

                for (int i = 0; i < TableHeap.TableCount; i++)
                {
                    var table = (TableHeap.Table)i;
                    if (!heap.HasTable(table))
                        continue;

                    int size;
                    switch (table)
                    {
                        case TableHeap.Table.Module:
                            size = 2 // Generation
                                   + stridxSize // TypeName
                                   + (guididxSize * 3); // Mvid, EncId, EncBaseId
                            break;
                        case TableHeap.Table.TypeRef:
                            size = GetCodedIndexSize(CodedIndex.ResolutionScope) // ResolutionScope
                                   + (stridxSize * 2); // TypeName, Namespace
                            break;
                        case TableHeap.Table.TypeDef:
                            size = 4 // Flags
                                   + (stridxSize * 2) // TypeName, Namespace
                                   + GetCodedIndexSize(CodedIndex.TypeDefOrRef) // BaseType
                                   + GetTableIndexSize(TableHeap.Table.Field) // FieldList
                                   + GetTableIndexSize(TableHeap.Table.Method); // MethodList
                            break;
                        case TableHeap.Table.FieldPtr:
                            size = GetTableIndexSize(TableHeap.Table.Field); // Field
                            break;
                        case TableHeap.Table.Field:
                            size = 2 // Flags
                                   + stridxSize // TypeName
                                   + blobidxSize; // Signature
                            break;
                        case TableHeap.Table.MethodPtr:
                            size = GetTableIndexSize(TableHeap.Table.Method); // Method
                            break;
                        case TableHeap.Table.Method:
                            size = 8 // Rva 4, ImplFlags 2, Flags 2
                                   + stridxSize // TypeName
                                   + blobidxSize // Signature
                                   + GetTableIndexSize(TableHeap.Table.Param); // ParamList
                            break;
                        case TableHeap.Table.ParamPtr:
                            size = GetTableIndexSize(TableHeap.Table.Param); // Param
                            break;
                        case TableHeap.Table.Param:
                            size = 4 // Flags 2, Sequence 2
                                   + stridxSize; // TypeName
                            break;
                        case TableHeap.Table.InterfaceImpl:
                            size = GetTableIndexSize(TableHeap.Table.TypeDef) // Class
                                   + GetCodedIndexSize(CodedIndex.TypeDefOrRef); // Interface
                            break;
                        case TableHeap.Table.MemberRef:
                            size = GetCodedIndexSize(CodedIndex.MemberRefParent) // Class
                                   + stridxSize // TypeName
                                   + blobidxSize; // Signature
                            break;
                        case TableHeap.Table.Constant:
                            size = 2 // Type
                                   + GetCodedIndexSize(CodedIndex.HasConstant) // Parent
                                   + blobidxSize; // Value
                            break;
                        case TableHeap.Table.CustomAttribute:
                            size = GetCodedIndexSize(CodedIndex.HasCustomAttribute) // Parent
                                   + GetCodedIndexSize(CodedIndex.CustomAttributeType) // Type
                                   + blobidxSize; // Value
                            break;
                        case TableHeap.Table.FieldMarshal:
                            size = GetCodedIndexSize(CodedIndex.HasFieldMarshal) // Parent
                                   + blobidxSize; // NativeType
                            break;
                        case TableHeap.Table.DeclSecurity:
                            size = 2 // Action
                                   + GetCodedIndexSize(CodedIndex.HasDeclSecurity) // Parent
                                   + blobidxSize; // PermissionSet
                            break;
                        case TableHeap.Table.ClassLayout:
                            size = 6 // PackingSize 2, ClassSize 4
                                   + GetTableIndexSize(TableHeap.Table.TypeDef); // Parent
                            break;
                        case TableHeap.Table.FieldLayout:
                            size = 4 // Offset
                                   + GetTableIndexSize(TableHeap.Table.Field); // Field
                            break;
                        case TableHeap.Table.StandAloneSig:
                            size = blobidxSize; // Signature
                            break;
                        case TableHeap.Table.EventMap:
                            size = GetTableIndexSize(TableHeap.Table.TypeDef) // Parent
                                   + GetTableIndexSize(TableHeap.Table.Event); // EventList
                            break;
                        case TableHeap.Table.EventPtr:
                            size = GetTableIndexSize(TableHeap.Table.Event); // Event
                            break;
                        case TableHeap.Table.Event:
                            size = 2 // Flags
                                   + stridxSize // TypeName
                                   + GetCodedIndexSize(CodedIndex.TypeDefOrRef); // EventType
                            break;
                        case TableHeap.Table.PropertyMap:
                            size = GetTableIndexSize(TableHeap.Table.TypeDef) // Parent
                                   + GetTableIndexSize(TableHeap.Table.Property); // PropertyList
                            break;
                        case TableHeap.Table.PropertyPtr:
                            size = GetTableIndexSize(TableHeap.Table.Property); // Property
                            break;
                        case TableHeap.Table.Property:
                            size = 2 // Flags
                                   + stridxSize // TypeName
                                   + blobidxSize; // Type
                            break;
                        case TableHeap.Table.MethodSemantics:
                            size = 2 // Semantics
                                   + GetTableIndexSize(TableHeap.Table.Method) // Method
                                   + GetCodedIndexSize(CodedIndex.HasSemantics); // Association
                            break;
                        case TableHeap.Table.MethodImpl:
                            size = GetTableIndexSize(TableHeap.Table.TypeDef) // Class
                                   + GetCodedIndexSize(CodedIndex.MethodDefOrRef) // MethodBody
                                   + GetCodedIndexSize(CodedIndex.MethodDefOrRef); // MethodDeclaration
                            break;
                        case TableHeap.Table.ModuleRef:
                            size = stridxSize; // TypeName
                            break;
                        case TableHeap.Table.TypeSpec:
                            size = blobidxSize; // Signature
                            break;
                        case TableHeap.Table.ImplMap:
                            size = 2 // MappingFlags
                                   + GetCodedIndexSize(CodedIndex.MemberForwarded) // MemberForwarded
                                   + stridxSize // ImportName
                                   + GetTableIndexSize(TableHeap.Table.ModuleRef); // ImportScope
                            break;
                        case TableHeap.Table.FieldRva:
                            size = 4 // RVA
                                   + GetTableIndexSize(TableHeap.Table.Field); // Field
                            break;
                        case TableHeap.Table.EncLog:
                            size = 8;
                            break;
                        case TableHeap.Table.EncMap:
                            size = 4;
                            break;
                        case TableHeap.Table.Assembly:
                            size = 16 // HashAlgId 4, Version 4 * 2, Flags 4
                                   + blobidxSize // PublicKey
                                   + (stridxSize * 2); // TypeName, Culture
                            break;
                        case TableHeap.Table.AssemblyProcessor:
                            size = 4; // Processor
                            break;
                        case TableHeap.Table.AssemblyOS:
                            size = 12; // Platform 4, Version 2 * 4
                            break;
                        case TableHeap.Table.AssemblyRef:
                            size = 12 // Version 2 * 4 + Flags 4
                                   + (blobidxSize * 2) // PublicKeyOrToken, HashValue
                                   + (stridxSize * 2); // TypeName, Culture
                            break;
                        case TableHeap.Table.AssemblyRefProcessor:
                            size = 4 // Processor
                                   + GetTableIndexSize(TableHeap.Table.AssemblyRef); // AssemblyRef
                            break;
                        case TableHeap.Table.AssemblyRefOS:
                            size = 12 // Platform 4, Version 2 * 4
                                   + GetTableIndexSize(TableHeap.Table.AssemblyRef); // AssemblyRef
                            break;
                        case TableHeap.Table.File:
                            size = 4 // Flags
                                   + stridxSize // TypeName
                                   + blobidxSize; // HashValue
                            break;
                        case TableHeap.Table.ExportedType:
                            size = 8 // Flags 4, TypeDefId 4
                                   + (stridxSize * 2) // TypeName, Namespace
                                   + GetCodedIndexSize(CodedIndex.Implementation); // Implementation
                            break;
                        case TableHeap.Table.ManifestResource:
                            size = 8 // Offset, Flags
                                   + stridxSize // TypeName
                                   + GetCodedIndexSize(CodedIndex.Implementation); // Implementation
                            break;
                        case TableHeap.Table.NestedClass:
                            size = GetTableIndexSize(TableHeap.Table.TypeDef) // NestedClass
                                   + GetTableIndexSize(TableHeap.Table.TypeDef); // EnclosingClass
                            break;
                        case TableHeap.Table.GenericParam:
                            size = 4 // Number, Flags
                                   + GetCodedIndexSize(CodedIndex.TypeOrMethodDef) // Owner
                                   + stridxSize; // TypeName
                            break;
                        case TableHeap.Table.MethodSpec:
                            size = GetCodedIndexSize(CodedIndex.MethodDefOrRef) // Method
                                   + blobidxSize; // Instantiation
                            break;
                        case TableHeap.Table.GenericParamConstraint:
                            size = GetTableIndexSize(TableHeap.Table.GenericParam) // Owner
                                   + GetCodedIndexSize(CodedIndex.TypeDefOrRef); // Constraint
                            break;
                        case TableHeap.Table.Document:
                            size = blobidxSize // TypeName
                                   + guididxSize // HashAlgorithm
                                   + blobidxSize // Hash
                                   + guididxSize; // Language
                            break;
                        case TableHeap.Table.MethodDebugInformation:
                            size = GetTableIndexSize(TableHeap.Table.Document) // Document
                                   + blobidxSize; // SequencePoints
                            break;
                        case TableHeap.Table.LocalScope:
                            size = GetTableIndexSize(TableHeap.Table.Method) // Method
                                   + GetTableIndexSize(TableHeap.Table.ImportScope) // ImportScope
                                   + GetTableIndexSize(TableHeap.Table.LocalVariable) // VariableList
                                   + GetTableIndexSize(TableHeap.Table.LocalConstant) // ConstantList
                                   + 4 * 2; // StartOffset, Length
                            break;
                        case TableHeap.Table.LocalVariable:
                            size = 2 // Attributes
                                   + 2 // Index
                                   + stridxSize; // TypeName
                            break;
                        case TableHeap.Table.LocalConstant:
                            size = stridxSize // TypeName
                                   + blobidxSize; // Signature
                            break;
                        case TableHeap.Table.ImportScope:
                            size = GetTableIndexSize(TableHeap.Table.ImportScope) // Parent
                                   + blobidxSize;
                            break;
                        case TableHeap.Table.StateMachineMethod:
                            size = GetTableIndexSize(TableHeap.Table.Method) // MoveNextMethod
                                   + GetTableIndexSize(TableHeap.Table.Method); // KickOffMethod
                            break;
                        case TableHeap.Table.CustomDebugInformation:
                            size = GetCodedIndexSize(CodedIndex.HasCustomDebugInformation) // Parent
                                   + guididxSize // Kind
                                   + blobidxSize; // Value
                            break;
                        default:
                            throw new NotSupportedException();
                    }

                    tables[i].RowSize = (uint)size;
                    tables[i].Offset = offset;

                    offset += (uint)size * tables[i].Length;
                }

                int GetTableIndexSize(TableHeap.Table table)
                {
                    return Image.TableHeap[table].Length < 65536 ? 2 : 4;
                }

                int GetCodedIndexSize(CodedIndex index)
                {
                    var i = (int)index;
                    int size = Image._codedIndexSizes[i];
                    if (size != 0) return size;

                    Image._codedIndexSizes[i] = index.GetSize(table => (int)Image.TableHeap[table].Length);
                    return Image._codedIndexSizes[i];
                }
            }
        }
    }
}