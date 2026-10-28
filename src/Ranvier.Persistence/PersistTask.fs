namespace Ranvier.Persistence

open System.Threading.Tasks
#if FABLE_COMPILER
open Fable.Core
#endif

/// <summary>Task operations with one meaning on .NET and under Fable, where a <c>Task</c> is a <c>Promise</c>.</summary>
module internal PersistTask =
    /// <summary>A finished task.</summary>
    let completed () : Task =
#if FABLE_COMPILER
        Task.FromResult () :> Task
#else
        Task.CompletedTask
#endif

    /// <summary>A finished task holding <c>value</c>.</summary>
    let result (value: 'T) : Task<'T> =
        Task.FromResult value

#if FABLE_COMPILER
    /// <summary>The task of <c>f</c> applied to the result of <c>work</c>; a raise in <c>f</c> faults it.</summary>
    [<Emit("$0.then($1)")>]
    let map (work: Task<'T>) (f: 'T -> 'U) : Task<'U> = jsNative

    /// <summary>The task <c>next</c> returns for the result of <c>work</c>; a raise in <c>next</c> faults it.</summary>
    [<Emit("$0.then($1)")>]
    let chain (work: Task<'T>) (next: 'T -> Task<'U>) : Task<'U> = jsNative

    /// <summary>The task <c>next</c> returns for the result of <c>work</c>; a fault of <c>work</c> faults it.</summary>
    [<Emit("$0.then($1)")>]
    let bind (work: Task<'T>) (next: 'T -> Task) : Task = jsNative

    /// <summary>The task <c>next</c> returns, started once <c>tail</c> has completed in any state.</summary>
    [<Emit("$0.then($1, $1)")>]
    let after (tail: Task) (next: unit -> Task) : Task = jsNative

    /// <summary>The task <c>next</c> returns, started once <c>first</c> has succeeded; a fault of <c>first</c> faults it.</summary>
    [<Emit("$0().then($1)")>]
    let andThen (first: unit -> Task) (next: unit -> Task) : Task = jsNative

    /// <summary>
    /// A task that completes once <c>earlier</c> and <c>later</c> have both completed, faulting with the fault of
    /// <c>earlier</c>, or else with the fault of <c>later</c>.
    /// </summary>
    [<Emit("$0.then(() => $1, e => $1.then(() => Promise.reject(e), () => Promise.reject(e)))")>]
    let both (earlier: Task) (later: Task) : Task = jsNative
#else
    /// <summary>The task of <c>f</c> applied to the result of <c>work</c>; a raise in <c>f</c> faults it.</summary>
    let map (work: Task<'T>) (f: 'T -> 'U) : Task<'U> =
        task {
            let! value = work.ConfigureAwait false
            return f value
        }

    /// <summary>The task <c>next</c> returns for the result of <c>work</c>; a raise in <c>next</c> faults it.</summary>
    let chain (work: Task<'T>) (next: 'T -> Task<'U>) : Task<'U> =
        task {
            let! value = work.ConfigureAwait false
            return! (next value).ConfigureAwait false
        }

    /// <summary>The task <c>next</c> returns for the result of <c>work</c>; a fault of <c>work</c> faults it.</summary>
    let bind (work: Task<'T>) (next: 'T -> Task) : Task =
        task {
            let! value = work.ConfigureAwait false
            do! (next value).ConfigureAwait false
        }
        :> Task

    /// <summary>The task <c>next</c> returns, started once <c>tail</c> has completed in any state.</summary>
    let after (tail: Task) (next: unit -> Task) : Task =
        task {
            try
                do! tail.ConfigureAwait false
            with _ ->
                ()

            do! (next ()).ConfigureAwait false
        }
        :> Task

    /// <summary>The task <c>next</c> returns, started once <c>first</c> has succeeded; a fault of <c>first</c> faults it.</summary>
    let andThen (first: unit -> Task) (next: unit -> Task) : Task =
        task {
            do! (first ()).ConfigureAwait false
            do! (next ()).ConfigureAwait false
        }
        :> Task

    /// <summary>
    /// A task that completes once <c>earlier</c> and <c>later</c> have both completed, faulting with the fault of
    /// <c>earlier</c>, or else with the fault of <c>later</c>.
    /// </summary>
    let both (earlier: Task) (later: Task) : Task =
        task {
            try
                do! earlier.ConfigureAwait false
            with _ ->
                ()

            try
                do! later.ConfigureAwait false
            with _ ->
                ()

            if earlier.IsCompletedSuccessfully then
                do! later.ConfigureAwait false
            else
                do! earlier.ConfigureAwait false
        }
        :> Task
#endif
