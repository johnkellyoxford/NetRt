﻿// ReSharper disable InconsistentNaming

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Drawing;

#nullable enable

namespace NetJit.Representations
{
    // not thread safe by any definition of the word
    public class BasicBlock : IEnumerable<BasicBlock>
    {
        public BasicBlockFlags Flags { get; set; }
        public Memory<byte> Il { get; }
        public int Offset { get; }
        public int Length { get; }

        private BasicBlock? __backing_field__previous;
        private BasicBlock? __backing_field__next;

        public BasicBlock? Previous
        {
            get => __backing_field__previous;
            set
            {
                __backing_field__previous = value;
                if (value is object) value.__backing_field__next = this;
            }
        }

        public BasicBlock? Next
        {
            get => __backing_field__next;
            set
            {
                __backing_field__next = value;
                if (value is object) value.__backing_field__previous = this;
            }
        }

        public BasicBlock(BasicBlock? previous, BasicBlock? next, Memory<byte> method, int offset, int length)
        {
            Previous = previous;
            Next = next;
            Offset = offset;
            Length = length;
            Il = method.Slice(offset, length);
        }

        public Enumerator GetEnumerator() => new Enumerator(this);

        IEnumerator<BasicBlock> IEnumerable<BasicBlock>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        // for enumerator
        private BasicBlock(BasicBlock next) { __backing_field__next = next; }

        public struct Enumerator : IEnumerable<BasicBlock>, IEnumerator<BasicBlock>
        {
            private readonly BasicBlock _first;
            public Enumerator(BasicBlock first)
            {
                _first = first;
                Current = new BasicBlock(first);
            }

            public bool MoveNext()
            {
                Current = Current.Next!;
                return Current == null;
            }

            public void Reset()
            {
                Current = new BasicBlock(_first);
            }

            public BasicBlock Current { get; private set; }

            object IEnumerator.Current => Current;

            void IDisposable.Dispose()
            {
            }

            public Enumerator GetEnumerator() => this;

            IEnumerator<BasicBlock> IEnumerable<BasicBlock>.GetEnumerator() => GetEnumerator();

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }

    public enum BasicBlockFlags : ulong
    {

    }
}