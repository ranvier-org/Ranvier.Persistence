// How a test's TaskCompletionSource delivers its result under Fable.
//
// "promise": Fable's own TaskCompletionSource, a Promise. A settle reaches the engine on a later
//            microtask, as it does for every promise an application awaits.
// "inline":  the task is a synchronous thenable. A settle reaches the engine before SetResult
//            returns, as a .NET TaskCompletionSource created without RunContinuationsAsynchronously does.
//
// Both modes add the TaskCompletionSource members Fable's library omits: SetCanceled (Fable spells it
// SetCancelled) and the TrySet* family.
const Pending = 0, Resolved = 1, Rejected = 2;

class InlineTask {
    constructor() {
        this.state = Pending;
        this.value = undefined;
        this.waiting = [];
    }

    settle(state, value) {
        if (this.state !== Pending) return false;
        this.state = state;
        this.value = value;
        const waiting = this.waiting;
        this.waiting = [];
        for (const run of waiting) run();
        return true;
    }

    then(onResolved, onRejected) {
        const next = new InlineTask();
        const run = () => {
            const handler = this.state === Resolved ? onResolved : onRejected;
            if (typeof handler !== "function") {
                next.settle(this.state, this.value);
                return;
            }
            let result;
            try {
                result = handler(this.value);
            } catch (e) {
                next.settle(Rejected, e);
                return;
            }
            if (result != null && typeof result.then === "function")
                result.then(v => { next.settle(Resolved, v); }, e => { next.settle(Rejected, e); });
            else
                next.settle(Resolved, result);
        };
        if (this.state === Pending) this.waiting.push(run);
        else run();
        return next;
    }

    catch(onRejected) {
        return this.then(undefined, onRejected);
    }
}

let installed = null;

// `source` is a TaskCompletionSource of the compiled library.
export function install(mode, source) {
    if (installed !== null) return installed;
    installed = mode === "inline" ? "inline" : "promise";
    const proto = Object.getPrototypeOf(source);
    // The library's OperationCanceledException, the class the engine tests a rejection against.
    let OperationCanceledException;
    proto.SetCancelled.call({ _reject: e => { OperationCanceledException = e.constructor; } });

    if (installed === "inline") {
        const inline = tcs => tcs.inline ?? (tcs.inline = new InlineTask());
        proto.get_Task = function () { return inline(this); };
        proto.TrySetResult = function (v) { return inline(this).settle(Resolved, v); };
        proto.TrySetException = function (e) { return inline(this).settle(Rejected, e); };
        proto.TrySetCanceled = function () { return inline(this).settle(Rejected, new OperationCanceledException()); };
    } else {
        const settled = tcs => tcs.settled === true || ((tcs.settled = true), false);
        proto.TrySetResult = function (v) { if (settled(this)) return false; this._resolve(v); return true; };
        proto.TrySetException = function (e) { if (settled(this)) return false; this._reject(e); return true; };
        proto.TrySetCanceled = function () { if (settled(this)) return false; this._reject(new OperationCanceledException()); return true; };
    }

    proto.SetResult = function (v) { this.TrySetResult(v); };
    proto.SetException = function (e) { this.TrySetException(e); };
    proto.SetCancelled = function () { this.TrySetCanceled(); };
    proto.SetCanceled = proto.SetCancelled;
    return installed;
}
