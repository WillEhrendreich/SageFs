import { toString, Record, Union } from "./fable_modules/fable-library-js.4.29.0/Types.js";
import { array_type, int32_type, record_type, bool_type, union_type, option_type, string_type } from "./fable_modules/fable-library-js.4.29.0/Reflection.js";
import { printf, toText } from "./fable_modules/fable-library-js.4.29.0/String.js";
import { map as map_1, some, orElse, defaultArg } from "./fable_modules/fable-library-js.4.29.0/Option.js";
import { tryField } from "./JsHelpers.fs.js";
import { PromiseBuilder__Delay_62FBFDE1, PromiseBuilder__Run_212F1D4B } from "./fable_modules/Fable.Promise.3.2.0/Promise.fs.js";
import { promise } from "./fable_modules/Fable.Promise.3.2.0/PromiseImpl.fs.js";
import { choose, map } from "./fable_modules/fable-library-js.4.29.0/Array.js";

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

export class SageFsStatus extends Record {
    constructor(connected, healthy, status) {
        super();
        this.connected = connected;
        this.healthy = healthy;
        this.status = status;
    }
}

export function SageFsStatus_$reflection() {
    return record_type("SageFs.Vscode.SageFsClient.SageFsStatus", [], SageFsStatus, () => [["connected", bool_type], ["healthy", option_type(bool_type)], ["status", option_type(string_type)]]);
}

export class SystemStatus extends Record {
    constructor(supervised, restartCount, version) {
        super();
        this.supervised = supervised;
        this.restartCount = (restartCount | 0);
        this.version = version;
    }
}

export function SystemStatus_$reflection() {
    return record_type("SageFs.Vscode.SageFsClient.SystemStatus", [], SystemStatus, () => [["supervised", bool_type], ["restartCount", int32_type], ["version", string_type]]);
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
    constructor(mcpPort, dashboardPort) {
        super();
        this.mcpPort = (mcpPort | 0);
        this.dashboardPort = (dashboardPort | 0);
    }
}

export function Client_$reflection() {
    return record_type("SageFs.Vscode.SageFsClient.Client", [], Client, () => [["mcpPort", int32_type], ["dashboardPort", int32_type]]);
}

export function create(mcpPort, dashboardPort) {
    return new Client(mcpPort, dashboardPort);
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
    return new Promise((resolve, reject) => { const http = require('http'); const req = http.get(((arg = baseUrl(c), toText(printf("%s%s"))(arg)(path))), { timeout: timeout }, (res) => { let data = ''; res.on('data', (chunk) => data += chunk); res.on('end', () => resolve({ statusCode: res.statusCode || 0, body: data })); }); req.on('error', reject); req.on('timeout', () => { req.destroy(); reject(new Error('timeout')); }); });
}

export function httpPost(c, path, body, timeout) {
    let arg;
    return new Promise((resolve, reject) => { const http = require('http'); const url = new URL((arg = baseUrl(c), toText(printf("%s%s"))(arg)(path))); const req = http.request({ hostname: url.hostname, port: url.port, path: url.pathname, method: 'POST', headers: { 'Content-Type': 'application/json' }, timeout: timeout }, (res) => { let data = ''; res.on('data', (chunk) => data += chunk); res.on('end', () => resolve({ statusCode: res.statusCode || 0, body: data })); }); req.on('error', reject); req.on('timeout', () => { req.destroy(); reject(new Error('timeout')); }); req.write(body); req.end(); });
}

export function dashHttpGet(c, path, timeout) {
    let arg;
    return new Promise((resolve, reject) => { const http = require('http'); const req = http.get(((arg = (c.dashboardPort | 0), toText(printf("http://localhost:%d%s"))(arg)(path))), { timeout: timeout }, (res) => { let data = ''; res.on('data', (chunk) => data += chunk); res.on('end', () => resolve({ statusCode: res.statusCode || 0, body: data })); }); req.on('error', reject); req.on('timeout', () => { req.destroy(); reject(new Error('timeout')); }); });
}

export function dashHttpPost(c, path, body, timeout) {
    let arg;
    return new Promise((resolve, reject) => { const http = require('http'); const url = new URL((arg = (c.dashboardPort | 0), toText(printf("http://localhost:%d%s"))(arg)(path))); const req = http.request({ hostname: url.hostname, port: url.port, path: url.pathname, method: 'POST', headers: { 'Content-Type': 'application/json' }, timeout: timeout }, (res) => { let data = ''; res.on('data', (chunk) => data += chunk); res.on('end', () => resolve({ statusCode: res.statusCode || 0, body: data })); }); req.on('error', reject); req.on('timeout', () => { req.destroy(); reject(new Error('timeout')); }); req.write(body); req.end(); });
}

export function parseOutcome(parsed) {
    if (defaultArg(tryField("success", parsed), false)) {
        return new ApiOutcome(0, [orElse(tryField("message", parsed), tryField("result", parsed))]);
    }
    else {
        return new ApiOutcome(1, [defaultArg(tryField("error", parsed), "Unknown error")]);
    }
}

/**
 * POST a command, parse the standard { success, message/result, error } response.
 */
export function postCommand(c, path, body, timeout) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => (httpPost(c, path, body, timeout).then((_arg) => (Promise.resolve(parseOutcome(JSON.parse(_arg.body))))))).catch((_arg_1) => (Promise.resolve(new ApiOutcome(1, [toString(_arg_1)])))))));
}

/**
 * GET from MCP port, parse JSON on 200, None otherwise.
 */
export function getJson(ctx, path, timeout, parse, c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => (httpGet(c, path, timeout).then((_arg) => {
        const resp = _arg;
        return (resp.statusCode === 200) ? (Promise.resolve(some(parse(JSON.parse(resp.body))))) : (Promise.resolve(undefined));
    }))).catch((_arg_1) => {
        console.warn('[SageFs]', ctx, _arg_1);
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
        console.warn('[SageFs]', ctx, _arg_1);
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
        console.warn('[SageFs]', ctx, _arg_1);
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
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => (httpGet(c, "/health", 3000).then((_arg) => (Promise.resolve(_arg.statusCode > 0))))).catch((_arg_1) => (Promise.resolve(false))))));
}

export function getStatus(c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => (httpGet(c, "/health", 3000).then((_arg) => {
        const resp = _arg;
        if (resp.statusCode === 200) {
            const parsed = JSON.parse(resp.body);
            return Promise.resolve(new SageFsStatus(true, orElse(tryField("healthy", parsed), false), tryField("status", parsed)));
        }
        else {
            return Promise.resolve(new SageFsStatus(true, false, "no session"));
        }
    }))).catch((_arg_1) => (Promise.resolve(new SageFsStatus(false, undefined, undefined)))))));
}

export function evalCode(code, workingDirectory, c) {
    const wd = defaultArg(workingDirectory, "");
    return postCommand(c, "/exec", JSON.stringify({
        code: code,
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
    return map((s) => (new SessionInfo(defaultArg(tryField("id", s), ""), undefined, defaultArg(tryField("workingDirectory", s), ""), defaultArg(tryField("status", s), "unknown"), defaultArg(tryField("projects", s), []), defaultArg(tryField("evalCount", s), 0))), defaultArg(tryField("sessions", parsed), []));
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
    return new SystemStatus(defaultArg(tryField("supervised", parsed), false), defaultArg(tryField("restartCount", parsed), 0), defaultArg(tryField("version", parsed), "?"));
}

export function getSystemStatus(c) {
    return getJson("getSystemStatus", "/api/system/status", 3000, parseSystemStatus, c);
}

export function parseHotReloadState(parsed) {
    let x;
    return new HotReloadState(defaultArg(map_1((rawFiles) => choose((f) => map_1((p) => (new HotReloadFile(p, defaultArg(tryField("watched", f), false))), tryField("path", f)), rawFiles), (x = parsed.files, (x == null) ? undefined : some(x))), []), defaultArg(tryField("watchedCount", parsed), 0));
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
    const assemblies = map((a) => (new LoadedAssemblyInfo(defaultArg(tryField("Name", a), ""), defaultArg(tryField("Path", a), ""), defaultArg(tryField("NamespaceCount", a), 0), defaultArg(tryField("ModuleCount", a), 0))), defaultArg(tryField("AssembliesLoaded", parsed), []));
    const opened = map((b) => (new OpenedBindingInfo(defaultArg(tryField("Name", b), ""), defaultArg(tryField("IsModule", b), false), defaultArg(tryField("Source", b), ""))), defaultArg(tryField("NamespacesOpened", parsed), []));
    const failed = map((f) => {
        let x;
        return defaultArg(map_1((value_10) => value_10, (x = f, (x == null) ? undefined : some(x))), []);
    }, defaultArg(tryField("FailedOpens", parsed), []));
    return new WarmupContextInfo(defaultArg(tryField("SourceFilesScanned", parsed), 0), assemblies, opened, failed, defaultArg(tryField("WarmupDurationMs", parsed), 0));
}

export function getWarmupContext(sessionId, c) {
    return dashGetJson("getWarmupContext", toText(printf("/api/sessions/%s/warmup-context"))(sessionId), 5000, parseWarmupContext, c);
}

export class CompletionResult extends Record {
    constructor(label, kind, insertText) {
        super();
        this.label = label;
        this.kind = kind;
        this.insertText = insertText;
    }
}

export function CompletionResult_$reflection() {
    return record_type("SageFs.Vscode.SageFsClient.CompletionResult", [], CompletionResult, () => [["label", string_type], ["kind", string_type], ["insertText", string_type]]);
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
                const items = defaultArg(tryField("completions", JSON.parse(resp.body)), []);
                return Promise.resolve(map((item) => (new CompletionResult(defaultArg(tryField("label", item), ""), defaultArg(tryField("kind", item), ""), defaultArg(tryField("insertText", item), ""))), items));
            }
            else {
                return Promise.resolve([]);
            }
        });
    }).catch((_arg_1) => {
        console.warn('[SageFs]', "getCompletions", _arg_1);
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
        console.warn('[SageFs]', "explore", _arg_1);
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

export function getPipelineTrace(c) {
    return getRaw("getPipelineTrace", "/api/live-testing/pipeline-trace", 5000, c);
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

