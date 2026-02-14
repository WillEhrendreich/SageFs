module SageFs.Middleware.FsiCompatibility

open SageFs.AppState

// Middleware to automatically rewrite F# code patterns that don't work in FSI
// but work in compiled code, making user code "just work" without modifications

let fsiCompatibilityMiddleware next (request, st: AppState) =
  let code = request.Code
  
  let rewrittenCode = SageFs.FsiRewrite.rewriteInlineUseStatements code
  
  if rewrittenCode <> code then
    st.Logger.LogDebug "FSI Compatibility: Rewrote 'use' to 'let' for FSI compatibility"
  
  let newRequest = { request with Code = rewrittenCode }
  next (newRequest, st)
