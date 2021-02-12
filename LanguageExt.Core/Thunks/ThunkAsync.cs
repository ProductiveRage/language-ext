using System;
using System.Diagnostics.Contracts;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using LanguageExt.Common;

namespace LanguageExt.Thunks
{
    /// <summary>
    /// Lazily evaluates an asynchronous function and then memoizes the value
    /// </summary>
    /// <remarks>
    /// Highly optimised to reduce memory allocation as much as possible, needs no locks,
    /// and runs at most once.  Can be run again by creating a clone of the thunk and re-evaluating
    /// </remarks>
    public class ThunkAsync<A>
    {
        internal readonly Func<ValueTask<ThunkR<A>>> fun;
        internal volatile int state;
        internal Error error;
        internal A value;

        /// <summary>
        /// Construct a lazy thunk
        /// </summary>
        [Pure, MethodImpl(Thunk.mops)]
        public static ThunkAsync<A> Lazy(Func<ValueTask<ThunkR<A>>> fun) =>
            new ThunkAsync<A>(fun);

        /// <summary>
        /// Construct a lazy thunk
        /// </summary>
        [Pure, MethodImpl(Thunk.mops)]
        public static ThunkAsync<A> Lazy(Func<ValueTask<Fin<A>>> fun) =>
            new ThunkAsync<A>(async () => ThunkR<A>.Result(await fun().ConfigureAwait(false)));

        /// <summary>
        /// Construct a lazy ThunkAsync
        /// </summary>
        [Pure, MethodImpl(Thunk.mops)]
        public static ThunkAsync<A> Lazy(Func<ValueTask<A>> fun) =>
            new ThunkAsync<A>(async () => ThunkR<A>.Succ(await fun().ConfigureAwait(false)));

        /// <summary>
        /// Construct an error ThunkAsync
        /// </summary>
        [Pure, MethodImpl(Thunk.mops)]
        public static ThunkAsync<A> Fail(Error error) =>
            new ThunkAsync<A>(Thunk.IsFailed, error);

        /// <summary>
        /// Construct a success thunk
        /// </summary>
        [Pure, MethodImpl(Thunk.mops)]
        public static ThunkAsync<A> Success(A value) =>
            new ThunkAsync<A>(value);

        /// <summary>
        /// Construct a cancelled thunk
        /// </summary>
        [Pure, MethodImpl(Thunk.mops)]
        public static ThunkAsync<A> Cancelled() =>
            new ThunkAsync<A>(Thunk.IsCancelled, Error.New(Thunk.CancelledText));

        /// <summary>
        /// Success ctor
        /// </summary>
        [MethodImpl(Thunk.mops)]
        ThunkAsync(A value) =>
            (this.state, this.value) = (Thunk.IsSuccess, value);

        /// <summary>
        /// Failed / Cancelled constructor
        /// </summary>
        [MethodImpl(Thunk.mops)]
        ThunkAsync(int state, Error error) =>
            (this.state, this.error) = (state, error);

        /// <summary>
        /// Lazy constructor
        /// </summary>
        [MethodImpl(Thunk.mops)]
        ThunkAsync(Func<ValueTask<ThunkR<A>>> fun) =>
            this.fun = fun ?? throw new ArgumentNullException(nameof(value));
        
        /// <summary>
        /// Clone the thunk
        /// </summary>
        /// <remarks>For thunks that were created as pre-failed/pre-cancelled values (i.e. no delegate to run, just
        /// in a pure error state), then the clone will copy that state exactly.  For thunks that have been evaluated
        /// then a cloned thunk will reset the thunk to a non-evaluated state.  This also means any thunk that has been
        /// evaluated and failed would lose the failed status</remarks>
        /// <returns></returns>
        [Pure, MethodImpl(Thunk.mops)]
        public ThunkAsync<A> Clone() =>
            fun == null
                ? new ThunkAsync<A>(state, error)
                : new ThunkAsync<A>(fun);        

        /// <summary>
        /// Value accessor
        /// </summary>
        [Pure, MethodImpl(Thunk.mops)]
        public ValueTask<ThunkR<A>> Value() =>
            Eval();

        /// <summary>
        /// Functor map
        /// </summary>
        [Pure, MethodImpl(Thunk.mops)]
        public ThunkAsync<B> Map<B>(Func<A, B> f)
        {
            try
            {
                while (true)
                {
                    SpinIfEvaluating();

                    switch (state)
                    {
                        case Thunk.IsSuccess:
                            return ThunkAsync<B>.Success(f(value));

                        case Thunk.NotEvaluated:
                            return ThunkAsync<B>.Lazy(async () =>
                            {
                                var ev = await fun().ConfigureAwait(false);
                                if (ev.IsFail)
                                {
                                    return ev.Cast<B>();
                                }
                                else
                                {
                                    return ThunkR<B>.Succ(f(ev.Value.data.Right));
                                }
                            });

                        case Thunk.IsCancelled:
                            return ThunkAsync<B>.Cancelled();

                        case Thunk.IsFailed:
                            return ThunkAsync<B>.Fail(error);
                    }
                }
            }
            catch (Exception e)
            {
                return ThunkAsync<B>.Fail(e);
            }
        }
        
        /// <summary>
        /// Functor map
        /// </summary>
        [Pure, MethodImpl(Thunk.mops)]
        public ThunkAsync<B> BiMap<B>(Func<A, B> Succ, Func<Error, Error> Fail)
        {
            try
            {
                while (true)
                {
                    SpinIfEvaluating();

                    switch (state)
                    {
                        case Thunk.IsSuccess:
                            return ThunkAsync<B>.Success(Succ(value));

                        case Thunk.NotEvaluated:
                            return ThunkAsync<B>.Lazy(async () =>
                            {
                                var ev = await fun().ConfigureAwait(false);
                                if (ev.IsFail)
                                {
                                    return ThunkR<B>.Fail(Fail(ev.Value.Error));
                                }
                                else
                                {
                                    return ThunkR<B>.Succ(Succ(ev.Value.data.Right));
                                }
                            });

                        case Thunk.IsCancelled:
                            return ThunkAsync<B>.Fail(Error.New(Thunk.CancelledText));

                        case Thunk.IsFailed:
                            return ThunkAsync<B>.Fail(Fail(error));
                    }
                }
            }
            catch (Exception e)
            {
                return ThunkAsync<B>.Fail(Fail(e));
            }
        }
        
        /// <summary>
        /// Functor map
        /// </summary>
        [Pure]
        public ThunkAsync<B> MapAsync<B>(Func<A, ValueTask<B>> f)
        {
            try
            {
                while (true)
                {
                    SpinIfEvaluating();

                    switch (state)
                    {
                        case Thunk.IsSuccess:
                            return ThunkAsync<B>.Lazy(async () => ThunkR<B>.Succ(await f(value).ConfigureAwait(false)));

                        case Thunk.NotEvaluated:
                            return ThunkAsync<B>.Lazy(async () =>
                            {
                                var ev = await fun().ConfigureAwait(false);
                                if (ev.IsFail)
                                {
                                    return ev.Cast<B>();
                                }
                                else
                                {
                                    return ThunkR<B>.Succ(await f(ev.Value.data.Right).ConfigureAwait(false));
                                }
                            });

                        case Thunk.IsCancelled:
                            return ThunkAsync<B>.Cancelled();

                        case Thunk.IsFailed:
                            return ThunkAsync<B>.Fail(error);
                    }
                }
            }
            catch (Exception e)
            {
                return ThunkAsync<B>.Fail(e);
            }
        }
                
        /// <summary>
        /// Functor map
        /// </summary>
        [Pure]
        public ThunkAsync<B> BiMapAsync<B>(Func<A, ValueTask<B>> Succ, Func<Error, ValueTask<Error>> Fail)
        {
            try
            {
                while (true)
                {
                    SpinIfEvaluating();

                    switch (state)
                    {
                        case Thunk.IsSuccess:
                            return ThunkAsync<B>.Lazy(async () => ThunkR<B>.Succ(await Succ(value).ConfigureAwait(false)));

                        case Thunk.NotEvaluated:
                            return ThunkAsync<B>.Lazy(async () =>
                            {
                                var ev = await fun().ConfigureAwait(false);
                                if (ev.IsFail)
                                {
                                    return ThunkR<B>.Fail(await Fail(ev.Value.Error).ConfigureAwait(false));
                                }
                                else
                                {
                                    return ThunkR<B>.Succ(await Succ(ev.Value.data.Right).ConfigureAwait(false));
                                }
                            });

                        case Thunk.IsCancelled:
                            return ThunkAsync<B>.Lazy(async () => await Fail(Error.New(Thunk.CancelledText)).ConfigureAwait(false));

                        case Thunk.IsFailed:
                            return ThunkAsync<B>.Lazy(async () => await Fail(error).ConfigureAwait(false));
                    }
                }
            }
            catch (Exception e)
            {
                return ThunkAsync<B>.Lazy(async () => await Fail(e).ConfigureAwait(false));
            }
        }

        /// <summary>
        /// Evaluates the lazy function if necessary, returns the result if it's previously been evaluated
        /// The thread goes into a spin-loop if more than one thread tries to access the lazy value at once.
        /// This is to protect against multiple evaluations.  This technique allows for a lock free access
        /// the vast majority of the time.
        /// </summary>
        [Pure, MethodImpl(Thunk.mops)]
        async ValueTask<ThunkR<A>> Eval()
        {
            while (true)
            {
                if (Interlocked.CompareExchange(ref state, Thunk.Evaluating, Thunk.NotEvaluated) == Thunk.NotEvaluated)
                {
                    try
                    {
                        var vt = fun();
                        var ex = await vt.ConfigureAwait(false);
                        if (vt.CompletedSuccessfully())
                        {
                            if (ex.IsFail)
                            {
                                error = ex.Value.Error;
                                state = Thunk.IsFailed; // state update must be last thing before return
                                return ex;
                            }
                            else
                            {
                                value = ex.Value.Value;
                                state = Thunk.IsSuccess; // state update must be last thing before return
                                return ex;
                            }
                        }
                        else if (vt.IsCanceled)
                        {
                            error = Error.New(Thunk.CancelledText);
                            state = Thunk.IsCancelled; // state update must be last thing before return
                            return ThunkR<A>.Fail(error);
                        }
                        else
                        {
                            var e = vt.AsTask().Exception;
                            error = e;
                            state = Thunk.IsFailed; // state update must be last thing before return
                            return ThunkR<A>.Fail(error);
                        }
                    }
                    catch (Exception e)
                    {
                        error = e;
                        state = e.Message == Thunk.CancelledText // state update must be last thing before return
                            ? Thunk.IsCancelled
                            : Thunk.IsFailed; 
                        return ThunkR<A>.Fail(Error.New(e));
                    }
                }
                else
                {
                    SpinIfEvaluating();

                    // Once we're here we should have a result from the eval thread and
                    // so we can use `value` to return
                    switch (state)
                    {
                        case Thunk.NotEvaluated: 
                        case Thunk.Evaluating: 
                            continue;
                        case Thunk.IsSuccess: 
                            return ThunkR<A>.Succ(value);
                        case Thunk.IsCancelled: 
                            return ThunkR<A>.Fail(Error.New(Thunk.CancelledText));
                        case Thunk.IsFailed:
                            return ThunkR<A>.Fail(Error.New(error));
                        default:
                            throw new InvalidOperationException("should never happen");
                    }
                }
            }
        }
        
        /// <summary>
        /// Spin whilst it's running so we don't run the operation twice
        /// this will block obviously, but this event should be super rare
        /// and it's purely to stop race conditions with the eval
        /// </summary>
        [MethodImpl(Thunk.mops)]
        public void SpinIfEvaluating()
        {
            while (state == Thunk.Evaluating)
            {
                SpinWait sw = default;
                sw.SpinOnce();
            }
        }
 
        [Pure, MethodImpl(Thunk.mops)]
        public override string ToString() =>
            state switch
            {
                Thunk.NotEvaluated => "Not evaluated",
                Thunk.Evaluating => "Evaluating",
                Thunk.IsSuccess => $"Success({value})",
                Thunk.IsCancelled => $"Cancelled",
                Thunk.IsFailed => $"Failed({error})",
                _ => ""
            };
    }
}
