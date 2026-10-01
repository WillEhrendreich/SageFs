/// A generic function the app reaches through `MakeGenericMethod`, with type arguments chosen at run time.
///
/// A detour cannot list the instantiations a program makes by reflection, so an edit to the function
/// has to restart and say that. The route is captured once at startup, like the parity fixture's.
module ReflectFixture.Reflect

open System.Threading.Tasks

type Marker = class end

let genericReflectionTag<'T> (x: 'T) : string = "genericReflection:A" + string x

/// The type arguments come from values, not from the source, so nothing in the IL names them.
let private through (t: System.Type) (arg: obj) : string =
  let definition = typeof<Marker>.DeclaringType.GetMethod "genericReflectionTag"
  definition.MakeGenericMethod(t).Invoke(null, [| arg |]) :?> string

let genericReflectionCaller () : string =
  let first : obj = box 7
  let second : obj = box "s"
  through (first.GetType()) first + "|" + through (second.GetType()) second

/// A delegate bound at startup to two instantiations of a generic function. F# wraps a function used as a
/// value in a closure, so a delegate bound to the generic method itself (what C# does with a method group)
/// takes reflection to make here.
let genericDelegateTag<'T> (x: 'T) : string = "genericDelegate:A" + string x

let private delegateOver<'T> () : System.Func<'T, string> =
  System.Delegate.CreateDelegate(typeof<System.Func<'T, string>>, typeof<Marker>.DeclaringType.GetMethod("genericDelegateTag").MakeGenericMethod(typeof<'T>)) :?> System.Func<'T, string>

let genericDelegateString = delegateOver<string> ()

let genericDelegateInt = delegateOver<int> ()

let genericDelegateCaller () : string = genericDelegateString.Invoke "s" + "|" + genericDelegateInt.Invoke 7

let routes : (string * (unit -> Task<string>)) list =
  [ "genericReflection", (fun () -> Task.FromResult(genericReflectionCaller ()))
    "genericDelegate", (fun () -> Task.FromResult(genericDelegateCaller ())) ]
