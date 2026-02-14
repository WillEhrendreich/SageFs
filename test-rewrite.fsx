let testRewrite() =
    let code = """
let someFunc ctx =
    use session = ctx |> getSession
    let result = session.Query()
    result
"""
    let lines = code.Split('\n')
    let mutable rewritten = false
    
    let rewrittenLines = 
        lines |> Array.mapi (fun i line ->
            let trimmed = line.TrimStart()
            if trimmed.StartsWith("use ") && line.Length > trimmed.Length then
                rewritten <- true
                printfn "REWRITING: '%s'" line
                line.Replace("use ", "let ")
            else
                line
        )
    
    let result = System.String.Join("\n", rewrittenLines)
    printfn "Rewritten: %b" rewritten
    printfn "Result:\n%s" result
    result

testRewrite()
