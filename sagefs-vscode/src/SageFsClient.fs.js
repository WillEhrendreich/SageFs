import { toString, Record, Union } from "./fable_modules/fable-library-js.4.29.0/Types.js";
import { lambda_type, unit_type, array_type, list_type, int32_type, bool_type, record_type, union_type, option_type, string_type } from "./fable_modules/fable-library-js.4.29.0/Reflection.js";
import { substring, printf, toText } from "./fable_modules/fable-library-js.4.29.0/String.js";
import { httpPost as httpPost_1, httpGet as httpGet_1 } from "./http-helpers.js";
import { map, value as value_14, some, orElse, defaultArg } from "./fable_modules/fable-library-js.4.29.0/Option.js";
import { tryCastStringArray, fieldStringArray, fieldInt, fieldArray, tryCastString, fieldObj, fieldString, fieldBool } from "./SafeInterop.fs.js";
import { PromiseBuilder__Delay_62FBFDE1, PromiseBuilder__Run_212F1D4B } from "./fable_modules/Fable.Promise.3.2.0/Promise.fs.js";
import { promise } from "./fable_modules/Fable.Promise.3.2.0/PromiseImpl.fs.js";
import { min } from "./fable_modules/fable-library-js.4.29.0/Double.js";
import { empty, ofArray } from "./fable_modules/fable-library-js.4.29.0/List.js";
import { map as map_1, choose } from "./fable_modules/fable-library-js.4.29.0/Array.js";
import { FSharpResult$2 } from "./fable_modules/fable-library-js.4.29.0/Result.js";

export class ApiOutcome extends Union {
    constructor(tag, fields) {
        super();
        this.tag = tag;
        this.fields = fields;
    }
    cases() {
        return ["Succeeded", "Failed"];
    }
}

export function ApiOutcome_$reflection() {
    return union_type("SageFs.Vscode.SageFsClient.ApiOutcome", [], ApiOutcome, () => [[["message", option_type(string_type)]], [["error", string_type]]]);
}

export function ApiOutcomeModule_message(_arg) {
    if (_arg.tag === 1) {
        return undefined;
    }
    else {
        return _arg.fields[0];
    }
}

export function ApiOutcomeModule_error(_arg) {
    if (_arg.tag === 0) {
        return undefined;
    }
    else {
        return _arg.fields[0];
    }
}

export function ApiOutcomeModule_isOk(_arg) {
    if (_arg.tag === 1) {
        return false;
    }
    else {
        return true;
    }
}

export function ApiOutcomeModule_messageOrDefault(fallback, _arg) {
    if (_arg.tag === 1) {
        return _arg.fields[0];
    }
    else if (_arg.fields[0] == null) {
        return fallback;
    }
    else {
        const m = _arg.fields[0];
        return m;
    }
}

export class HealthError extends Record {
    constructor(case$, message, suggestedAction) {
        super();
        this.case = case$;
        this.message = message;
        this.suggestedAction = suggestedAction;
    }
}

export function HealthError_$reflection() {
    return record_type("SageFs.Vscode.SageFsClient.HealthError", [], HealthError, () => [["case", string_type], ["message", string_type], ["suggestedAction", string_type]]);
}

export class SageFsStatus extends Record {
    constructor(connected, healthy, status, apiVersion, features, error) {
        super();
        this.connected = connected;
        this.healthy = healthy;
        this.status = status;
        this.apiVersion = apiVersion;
        this.features = features;
        this.error = error;
    }
}

export function SageFsStatus_$reflection() {
    return record_type("SageFs.Vscode.SageFsClient.SageFsStatus", [], SageFsStatus, () => [["connected", bool_type], ["healthy", option_type(bool_type)], ["status", option_type(string_type)], ["apiVersion", option_type(int32_type)], ["features", list_type(string_type)], ["error", option_type(HealthError_$reflection())]]);
}

export class SystemStatus extends Record {
    constructor(supervised, restartCount, version, apiVersion) {
        super();
        this.supervised = supervised;
        this.restartCount = (restartCount | 0);
        this.version = version;
        this.apiVersion = (apiVersion | 0);
    }
}

export function SystemStatus_$reflection() {
    return record_type("SageFs.Vscode.SageFsClient.SystemStatus", [], SystemStatus, () => [["supervised", bool_type], ["restartCount", int32_type], ["version", string_type], ["apiVersion", int32_type]]);
}

export class HotReloadFile extends Record {
    constructor(path, watched) {
        super();
        this.path = path;
        this.watched = watched;
    }
}

export function HotReloadFile_$reflection() {
    return record_type("SageFs.Vscode.SageFsClient.HotReloadFile", [], HotReloadFile, () => [["path", string_type], ["watched", bool_type]]);
}

export class HotReloadState extends Record {
    constructor(files, watchedCount) {
        super();
        this.files = files;
        this.watchedCount = (watchedCount | 0);
    }
}

export function HotReloadState_$reflection() {
    return record_type("SageFs.Vscode.SageFsClient.HotReloadState", [], HotReloadState, () => [["files", array_type(HotReloadFile_$reflection())], ["watchedCount", int32_type]]);
}

export class SessionInfo extends Record {
    constructor(id, name, workingDirectory, status, projects, evalCount) {
        super();
        this.id = id;
        this.name = name;
        this.workingDirectory = workingDirectory;
        this.status = status;
        this.projects = projects;
        this.evalCount = (evalCount | 0);
    }
}

export function SessionInfo_$reflection() {
    return record_type("SageFs.Vscode.SageFsClient.SessionInfo", [], SessionInfo, () => [["id", string_type], ["name", option_type(string_type)], ["workingDirectory", string_type], ["status", string_type], ["projects", array_type(string_type)], ["evalCount", int32_type]]);
}

export class LoadedAssemblyInfo extends Record {
    constructor(Name, Path, NamespaceCount, ModuleCount) {
        super();
        this.Name = Name;
        this.Path = Path;
        this.NamespaceCount = (NamespaceCount | 0);
        this.ModuleCount = (ModuleCount | 0);
    }
}

export function LoadedAssemblyInfo_$reflection() {
    return record_type("SageFs.Vscode.SageFsClient.LoadedAssemblyInfo", [], LoadedAssemblyInfo, () => [["Name", string_type], ["Path", string_type], ["NamespaceCount", int32_type], ["ModuleCount", int32_type]]);
}

export class OpenedBindingInfo extends Record {
    constructor(Name, IsModule, Source) {
        super();
        this.Name = Name;
        this.IsModule = IsModule;
        this.Source = Source;
    }
}

export function OpenedBindingInfo_$reflection() {
    return record_type("SageFs.Vscode.SageFsClient.OpenedBindingInfo", [], OpenedBindingInfo, () => [["Name", string_type], ["IsModule", bool_type], ["Source", string_type]]);
}

export class WarmupContextInfo extends Record {
    constructor(SourceFilesScanned, AssembliesLoaded, NamespacesOpened, FailedOpens, WarmupDurationMs) {
        super();
        this.SourceFilesScanned = (SourceFilesScanned | 0);
        this.AssembliesLoaded = AssembliesLoaded;
        this.NamespacesOpened = NamespacesOpened;
        this.FailedOpens = FailedOpens;
        this.WarmupDurationMs = (WarmupDurationMs | 0);
    }
}

export function WarmupContextInfo_$reflection() {
    return record_type("SageFs.Vscode.SageFsClient.WarmupContextInfo", [], WarmupContextInfo, () => [["SourceFilesScanned", int32_type], ["AssembliesLoaded", array_type(LoadedAssemblyInfo_$reflection())], ["NamespacesOpened", array_type(OpenedBindingInfo_$reflection())], ["FailedOpens", array_type(array_type(string_type))], ["WarmupDurationMs", int32_type]]);
}

export class Client extends Record {
    constructor(mcpPort, dashboardPort, log) {
        super();
        this.mcpPort = (mcpPort | 0);
        this.dashboardPort = (dashboardPort | 0);
        this.log = log;
    }
}

export function Client_$reflection() {
    return record_type("SageFs.Vscode.SageFsClient.Client", [], Client, () => [["mcpPort", int32_type], ["dashboardPort", int32_type], ["log", lambda_type(string_type, unit_type)]]);
}

export function create(mcpPort, dashboardPort, log) {
    return new Client(mcpPort, dashboardPort, log);
}

export function baseUrl(c) {
    const arg = c.mcpPort | 0;
    return toText(printf("http://localhost:%d"))(arg);
}

export function dashboardUrl(c) {
    const arg = c.dashboardPort | 0;
    return toText(printf("http://localhost:%d/dashboard"))(arg);
}

export function updatePorts(mcpPort, dashboardPort, c) {
    c.mcpPort = (mcpPort | 0);
    c.dashboardPort = (dashboardPort | 0);
}

export function httpGet(c, path, timeout) {
    let arg;
    return httpGet_1((arg = baseUrl(c), toText(printf("%s%s"))(arg)(path)), timeout);
}

export function httpPost(c, path, body, timeout) {
    let arg;
    return httpPost_1((arg = baseUrl(c), toText(printf("%s%s"))(arg)(path)), body, timeout);
}

export function dashHttpGet(c, path, timeout) {
    let arg;
    return httpGet_1((arg = (c.dashboardPort | 0), toText(printf("http://localhost:%d%s"))(arg)(path)), timeout);
}

export function dashHttpPost(c, path, body, timeout) {
    let arg;
    return httpPost_1((arg = (c.dashboardPort | 0), toText(printf("http://localhost:%d%s"))(arg)(path)), body, timeout);
}

export function parseOutcome(parsed) {
    if (defaultArg(fieldBool("success", parsed), false)) {
        return new ApiOutcome(0, [orElse(fieldString("message", parsed), fieldString("result", parsed))]);
    }
    else {
        return new ApiOutcome(1, [defaultArg(fieldString("error", parsed), "Unknown error")]);
    }
}

/**
 * POST a command, parse the standard { success, message/result, error } response.
 */
export function postCommand(c, path, body, timeout) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => (httpPost(c, path, body, timeout).then((_arg) => {
        let s, arg_1;
        const resp = _arg;
        const matchValue = resp.statusCode | 0;
        return ((s = (matchValue | 0), (s >= 200) && (s < 300))) ? (Promise.resolve(parseOutcome(JSON.parse(resp.body)))) : (Promise.resolve(new ApiOutcome(1, [(arg_1 = substring(resp.body, 0, min(200, resp.body.length)), toText(printf("HTTP %d: %s"))(matchValue)(arg_1))])));
    }))).catch((_arg_1) => (Promise.resolve(new ApiOutcome(1, [toString(_arg_1)])))))));
}

/**
 * GET from MCP port, parse JSON on 200, None otherwise.
 */
export function getJson(ctx, path, timeout, parse, c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => (httpGet(c, path, timeout).then((_arg) => {
        const resp = _arg;
        return (resp.statusCode === 200) ? (Promise.resolve(some(parse(JSON.parse(resp.body))))) : (Promise.resolve(undefined));
    }))).catch((_arg_1) => {
        c.log(toText(printf("[warn] %s: %O"))(ctx)(_arg_1));
        return Promise.resolve(undefined);
    }))));
}

/**
 * GET raw body from MCP port on 200, None otherwise.
 */
export function getRaw(ctx, path, timeout, c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => (httpGet(c, path, timeout).then((_arg) => {
        const resp = _arg;
        return (resp.statusCode === 200) ? (Promise.resolve(resp.body)) : (Promise.resolve(undefined));
    }))).catch((_arg_1) => {
        c.log(toText(printf("[warn] %s: %O"))(ctx)(_arg_1));
        return Promise.resolve(undefined);
    }))));
}

/**
 * GET from dashboard port, parse JSON on 200, None otherwise.
 */
export function dashGetJson(ctx, path, timeout, parse, c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => (dashHttpGet(c, path, timeout).then((_arg) => {
        const resp = _arg;
        return (resp.statusCode === 200) ? (Promise.resolve(some(parse(JSON.parse(resp.body))))) : (Promise.resolve(undefined));
    }))).catch((_arg_1) => {
        c.log(toText(printf("[warn] %s: %O"))(ctx)(_arg_1));
        return Promise.resolve(undefined);
    }))));
}

/**
 * POST to dashboard port, succeed on 2xx, fail otherwise.
 */
export function dashPostOutcome(ctx, path, body, timeout, c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => (dashHttpPost(c, path, body, timeout).then((_arg) => {
        let s;
        const resp = _arg;
        return ((s = (resp.statusCode | 0), (s >= 200) && (s < 300))) ? (Promise.resolve(new ApiOutcome(0, [undefined]))) : (Promise.resolve(new ApiOutcome(1, [toText(printf("%s: HTTP %d"))(ctx)(resp.statusCode)])));
    }))).catch((_arg_1) => {
        let arg_3;
        return Promise.resolve(new ApiOutcome(1, [(arg_3 = toString(_arg_1), toText(printf("%s: %s"))(ctx)(arg_3))]));
    }))));
}

export function isRunning(c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => (httpGet(c, "/health", 15000).then((_arg) => (Promise.resolve(_arg.statusCode === 200))))).catch((_arg_1) => (Promise.resolve(false))))));
}

export function parseHealthError(parsed) {
    const errObj = fieldObj("error")(parsed);
    if (errObj != null) {
        const e = value_14(errObj);
        return new HealthError(defaultArg(fieldString("case", e), "Unknown"), defaultArg(fieldString("message", e), ""), defaultArg(fieldString("suggestedAction", e), ""));
    }
    else {
        return undefined;
    }
}

export function getStatus(c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => (httpGet(c, "/health", 15000).then((_arg) => {
        const resp = _arg;
        if (resp.statusCode === 200) {
            const parsed = JSON.parse(resp.body);
            const features = defaultArg(map((arg) => ofArray(choose(tryCastString, arg)), fieldArray("features", parsed)), empty());
            return Promise.resolve(new SageFsStatus(true, orElse(fieldBool("healthy", parsed), false), fieldString("status", parsed), fieldInt("apiVersion", parsed), features, parseHealthError(parsed)));
        }
        else {
            return Promise.resolve(new SageFsStatus(true, false, "no session", undefined, empty(), undefined));
        }
    }))).catch((_arg_1) => (Promise.resolve(new SageFsStatus(false, undefined, undefined, undefined, empty(), undefined)))))));
}

export function isReady(c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (getStatus(c).then((_arg) => {
        const s = _arg;
        const matchValue_1 = s.status;
        let matchResult;
        if (s.connected) {
            if (matchValue_1 != null) {
                switch (matchValue_1) {
                    case "Ready":
                    case "Evaluating": {
                        matchResult = 0;
                        break;
                    }
                    default:
                        matchResult = 1;
                }
            }
            else {
                matchResult = 1;
            }
        }
        else {
            matchResult = 1;
        }
        switch (matchResult) {
            case 0:
                return Promise.resolve(true);
            default:
                return Promise.resolve(false);
        }
    }))));
}

/**
 * Returns the features list from the daemon health endpoint, or empty list if unreachable.
 */
export function getFeatures(c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (getStatus(c).then((_arg) => (Promise.resolve(_arg.features))))));
}

export function evalCode(code, workingDirectory, filePath, evalMode, blockStartLine, c) {
    const wd = defaultArg(workingDirectory, "");
    const fp = defaultArg(filePath, "");
    const em = defaultArg(evalMode, "");
    const bsl = defaultArg(blockStartLine, 0) | 0;
    return postCommand(c, "/exec", JSON.stringify({
        block_start_line: bsl,
        code: code,
        eval_mode: em,
        file_path: fp,
        working_directory: wd,
    }), 30000);
}

export function resetSession(c) {
    return postCommand(c, "/reset", "{}", 15000);
}

export function hardReset(rebuild, c) {
    return postCommand(c, "/hard-reset", JSON.stringify({
        rebuild: rebuild,
    }), 60000);
}

export function parseSessions(parsed) {
    return map_1((s) => (new SessionInfo(defaultArg(fieldString("id", s), ""), undefined, defaultArg(fieldString("workingDirectory", s), ""), defaultArg(fieldString("status", s), "unknown"), defaultArg(fieldStringArray("projects", s), []), defaultArg(fieldInt("evalCount", s), 0))), defaultArg(fieldArray("sessions", parsed), []));
}

export function listSessions(c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (getJson("listSessions", "/api/sessions", 5000, parseSessions, c).then((_arg) => (Promise.resolve(defaultArg(_arg, [])))))));
}

export function createSession(projects, workingDirectory, c) {
    return postCommand(c, "/api/sessions/create", JSON.stringify({
        projects: [projects],
        workingDirectory: workingDirectory,
    }), 30000);
}

export function switchSession(sessionId, c) {
    return postCommand(c, "/api/sessions/switch", JSON.stringify({
        sessionId: sessionId,
    }), 5000);
}

export function stopSession(sessionId, c) {
    return postCommand(c, "/api/sessions/stop", JSON.stringify({
        sessionId: sessionId,
    }), 10000);
}

export function parseSystemStatus(parsed) {
    return new SystemStatus(defaultArg(fieldBool("supervised", parsed), false), defaultArg(fieldInt("restartCount", parsed), 0), defaultArg(fieldString("version", parsed), "?"), defaultArg(fieldInt("apiVersion", parsed), 0));
}

export function checkVersion(status) {
    const matchValue = status.apiVersion | 0;
    if (matchValue === 1) {
        return new FSharpResult$2(0, [undefined]);
    }
    else {
        return new FSharpResult$2(1, [`SageFs daemon apiVersion=${matchValue} is incompatible with this extension (requires apiVersion=${1}). Run 'dotnet tool update --global SageFs' then reload VS Code.`]);
    }
}

export function getSystemStatus(c) {
    return getJson("getSystemStatus", "/api/system/status", 15000, parseSystemStatus, c);
}

export function parseHotReloadState(parsed) {
    return new HotReloadState(defaultArg(map((rawFiles) => choose((f) => map((p) => (new HotReloadFile(p, defaultArg(fieldBool("watched", f), false))), fieldString("path", f)), rawFiles), fieldArray("files", parsed)), []), defaultArg(fieldInt("watchedCount", parsed), 0));
}

export function getHotReloadState(sessionId, c) {
    return dashGetJson("getHotReloadState", toText(printf("/api/sessions/%s/hotreload"))(sessionId), 5000, parseHotReloadState, c);
}

export function toggleHotReload(sessionId, path, c) {
    return dashPostOutcome("toggleHotReload", toText(printf("/api/sessions/%s/hotreload/toggle"))(sessionId), JSON.stringify({
        path: path,
    }), 5000, c);
}

export function watchAllHotReload(sessionId, c) {
    return dashPostOutcome("watchAllHotReload", toText(printf("/api/sessions/%s/hotreload/watch-all"))(sessionId), "{}", 5000, c);
}

export function unwatchAllHotReload(sessionId, c) {
    return dashPostOutcome("unwatchAllHotReload", toText(printf("/api/sessions/%s/hotreload/unwatch-all"))(sessionId), "{}", 5000, c);
}

export function watchDirectoryHotReload(sessionId, directory, c) {
    return dashPostOutcome("watchDirectoryHotReload", toText(printf("/api/sessions/%s/hotreload/watch-directory"))(sessionId), JSON.stringify({
        directory: directory,
    }), 5000, c);
}

export function unwatchDirectoryHotReload(sessionId, directory, c) {
    return dashPostOutcome("unwatchDirectoryHotReload", toText(printf("/api/sessions/%s/hotreload/unwatch-directory"))(sessionId), JSON.stringify({
        directory: directory,
    }), 5000, c);
}

export function parseWarmupContext(parsed) {
    const assemblies = map_1((a) => (new LoadedAssemblyInfo(defaultArg(fieldString("name", a), ""), defaultArg(fieldString("path", a), ""), defaultArg(fieldInt("namespaceCount", a), 0), defaultArg(fieldInt("moduleCount", a), 0))), defaultArg(fieldArray("assembliesLoaded", parsed), []));
    const opened = map_1((b) => (new OpenedBindingInfo(defaultArg(fieldString("name", b), ""), defaultArg(fieldBool("isModule", b), false), defaultArg(fieldString("source", b), ""))), defaultArg(fieldArray("namespacesOpened", parsed), []));
    const failed = map_1((f) => defaultArg(tryCastStringArray(f), []), defaultArg(fieldArray("failedOpens", parsed), []));
    const timing = fieldObj("phaseTiming")(parsed);
    const totalMs = ((timing != null) ? defaultArg(fieldInt("totalMs", value_14(timing)), 0) : defaultArg(fieldInt("warmupDurationMs", parsed), 0)) | 0;
    return new WarmupContextInfo(defaultArg(fieldInt("sourceFilesScanned", parsed), 0), assemblies, opened, failed, totalMs);
}

export function getWarmupContext(sessionId, c) {
    return dashGetJson("getWarmupContext", toText(printf("/api/sessions/%s/warmup-context"))(sessionId), 5000, parseWarmupContext, c);
}

export class CompletionResult extends Record {
    constructor(label, kind, insertText, detail) {
        super();
        this.label = label;
        this.kind = kind;
        this.insertText = insertText;
        this.detail = detail;
    }
}

export function CompletionResult_$reflection() {
    return record_type("SageFs.Vscode.SageFsClient.CompletionResult", [], CompletionResult, () => [["label", string_type], ["kind", string_type], ["insertText", string_type], ["detail", option_type(string_type)]]);
}

export function getCompletions(code, cursorPosition, workingDirectory, c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => {
        const payload = {
            code: code,
            cursor_position: cursorPosition,
            working_directory: defaultArg(workingDirectory, ""),
        };
        return dashHttpPost(c, "/dashboard/completions", JSON.stringify(payload), 10000).then((_arg) => {
            const resp = _arg;
            if (resp.statusCode === 200) {
                const items = defaultArg(fieldArray("completions", JSON.parse(resp.body)), []);
                return Promise.resolve(map_1((item) => (new CompletionResult(defaultArg(fieldString("label", item), ""), defaultArg(fieldString("kind", item), ""), defaultArg(fieldString("insertText", item), ""), fieldString("detail", item))), items));
            }
            else {
                return Promise.resolve([]);
            }
        });
    }).catch((_arg_1) => {
        c.log(toText(printf("[warn] getCompletions: %O"))(_arg_1));
        return Promise.resolve([]);
    }))));
}

export function runTests(pattern, c) {
    return postCommand(c, "/api/live-testing/run", JSON.stringify({
        category: "",
        pattern: pattern,
    }), 60000);
}

export function enableLiveTesting(c) {
    return postCommand(c, "/api/live-testing/enable", "{}", 5000);
}

export function disableLiveTesting(c) {
    return postCommand(c, "/api/live-testing/disable", "{}", 5000);
}

export function setRunPolicy(category, policy, c) {
    return postCommand(c, "/api/live-testing/policy", JSON.stringify({
        category: category,
        policy: policy,
    }), 5000);
}

export function explore(name, c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => (httpPost(c, "/api/explore", JSON.stringify({
        name: name,
    }), 10000).then((_arg) => {
        const resp = _arg;
        return (resp.statusCode === 200) ? (Promise.resolve(resp.body)) : (Promise.resolve(undefined));
    }))).catch((_arg_1) => {
        c.log(toText(printf("[warn] explore: %O"))(_arg_1));
        return Promise.resolve(undefined);
    }))));
}

/**
 * Call /dashboard/completions with "Name." to get structured JSON:
 * { completions: [{ label, kind, insertText, detail? }], count }
 */
export function exploreCompletions(qualifiedName, sessionId, c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => {
        const code = toText(printf("%s."))(qualifiedName);
        const body = JSON.stringify({
            code: code,
            cursorPos: code.length,
            sessionId: sessionId,
        });
        return dashHttpPost(c, "/dashboard/completions", body, 10000).then((_arg) => {
            const resp = _arg;
            if (resp.statusCode === 200) {
                return Promise.resolve(resp.body);
            }
            else {
                c.log(toText(printf("[warn] exploreCompletions %s: status %d"))(qualifiedName)(resp.statusCode));
                return Promise.resolve(undefined);
            }
        });
    }).catch((_arg_1) => {
        c.log(toText(printf("[warn] exploreCompletions: %O"))(_arg_1));
        return Promise.resolve(undefined);
    }))));
}

export function getRecentEvents(count, c) {
    return getRaw("getRecentEvents", toText(printf("/api/recent-events?count=%d"))(count), 10000, c);
}

export function getDependencyGraph(symbol, c) {
    let arg;
    return getRaw("getDependencyGraph", (symbol === "") ? "/api/dependency-graph" : ((arg = encodeURIComponent(symbol), toText(printf("/api/dependency-graph?symbol=%s"))(arg))), 10000, c);
}

export function cancelEval(c) {
    return postCommand(c, "/api/cancel-eval", "{}", 5000);
}

export function loadScript(filePath, c) {
    const code = toText(printf("#load @\"%s\";;"))(filePath);
    return postCommand(c, "/exec", JSON.stringify({
        code: code,
        working_directory: "",
    }), 30000);
}

export function getTestTrace(c) {
    return getRaw("getTestTrace", "/api/live-testing/test-trace", 5000, c);
}

export class ExportResult extends Record {
    constructor(content, evalCount) {
        super();
        this.content = content;
        this.evalCount = (evalCount | 0);
    }
}

export function ExportResult_$reflection() {
    return record_type("SageFs.Vscode.SageFsClient.ExportResult", [], ExportResult, () => [["content", string_type], ["evalCount", int32_type]]);
}

export function exportSessionAsFsx(sessionId, c) {
    let arg;
    return getJson("exportSessionAsFsx", (arg = encodeURIComponent(sessionId), toText(printf("/api/sessions/%s/export-fsx"))(arg)), 15000, (p) => p, c);
}

