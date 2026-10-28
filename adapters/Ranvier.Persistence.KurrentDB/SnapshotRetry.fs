namespace Ranvier.Persistence.KurrentDB

open System.Threading.Tasks
open KurrentDB.Client

/// <summary>Bounded retries for snapshot append revision conflicts.</summary>
module internal SnapshotRetry =
    /// <summary>Retries revision conflicts up to 32 attempts, pausing between conflicting appends.</summary>
    let run (pause: int -> Task) (operation: unit -> Task<unit>) =
        task {
            let mutable committed = false
            let mutable attempts = 0

            while not committed do
                attempts <- attempts + 1

                try
                    do! operation ()
                    committed <- true
                with :? WrongExpectedVersionException when attempts < 32 ->
                    do! pause attempts
        }
