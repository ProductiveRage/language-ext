using System;
using System.Diagnostics.Contracts;
using System.Runtime.CompilerServices;
using LanguageExt.Common;
using static LanguageExt.Prelude;

namespace LanguageExt.Thunks
{
    /// <summary>
    /// Thunk return
    /// </summary>
    public struct ThunkR<A>
    {
        public readonly Fin<A> Value;
        public readonly HashSet<IDisposable> Acquired;

        [MethodImpl(Thunk.mops)]
        public ThunkR(Fin<A> value, HashSet<IDisposable> acquired) =>
            (Value, Acquired) = (value, acquired);

        [Pure, MethodImpl(Thunk.mops)]
        public static ThunkR<A> Result(Fin<A> value) =>
            value.IsSucc
                ? Succ(value.Value)
                : Fail(value.Error);

        [Pure, MethodImpl(Thunk.mops)]
        public static ThunkR<A> Succ(A value) =>
            new ThunkR<A>(FinSucc(value), value is IDisposable d ? Prelude.HashSet(d) : default);

        [Pure, MethodImpl(Thunk.mops)]
        public static ThunkR<A> Fail(Error value) =>
            new ThunkR<A>(FinFail<A>(value), default);

        [Pure, MethodImpl(Thunk.mops)]
        public static ThunkR<A> Fail(Exception value) =>
            new ThunkR<A>(FinFail<A>(value), default);

        [Pure, MethodImpl(Thunk.mops)]
        public static ThunkR<A> Fail(string value) =>
            new ThunkR<A>(FinFail<A>(Common.Error.New(value)), default);
        
        public bool IsFail
        {
            [Pure, MethodImpl(Thunk.mops)] 
            get => Value.IsFail;
        }
        
        public bool IsSucc
        {
            [Pure, MethodImpl(Thunk.mops)] 
            get => Value.IsSucc;
        }

        [Pure, MethodImpl(Thunk.mops)]
        public ThunkR<B> Cast<B>() =>
            new ThunkR<B>(Value.Cast<B>(), Acquired);
        
        public static Fin<A> DisposeAcquired(ThunkR<A> t)
        {
            if (!t.Acquired.IsEmpty)
            {
                foreach (var acq in t.Acquired)
                {
                    acq.Dispose();
                }
            }
            return t.Value;
        }

        public static ThunkR<A> operator +(ThunkR<A> ma, ThunkR<A> mb) =>
            new ThunkR<A>(ma.Value, ma.Acquired + mb.Acquired);
    }
}
