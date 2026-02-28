import { toString, Record } from "./fable_modules/fable-library-js.4.29.0/Types.js";
import { array_type, int32_type, record_type, option_type, string_type, bool_type } from "./fable_modules/fable-library-js.4.29.0/Reflection.js";
import { printf, toText } from "./fable_modules/fable-library-js.4.29.0/String.js";
import { PromiseBuilder__Delay_62FBFDE1, PromiseBuilder__Run_212F1D4B } from "./fable_modules/Fable.Promise.3.2.0/Promise.fs.js";
import { promise } from "./fable_modules/Fable.Promise.3.2.0/PromiseImpl.fs.js";
import { choose, map } from "./fable_modules/fable-library-js.4.29.0/Array.js";
import { defaultArg } from "./fable_modules/fable-library-js.4.29.0/Option.js";

export class EvalResult extends Record {
    constructor(success, result, error) {
        super();
        this.success = success;
        this.result = result;
        this.error = error;
    }
}

export function EvalResult_$reflection() {
    return record_type("SageFs.Vscode.SageFsClient.EvalResult", [], EvalResult, () => [["success", bool_type], ["result", option_type(string_type)], ["error", option_type(string_type)]]);
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

export function isRunning(c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => (httpGet(c, "/health", 3000).then((_arg) => (Promise.resolve(_arg.statusCode > 0))))).catch((_arg_1) => (Promise.resolve(false))))));
}

export function getStatus(c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => (httpGet(c, "/health", 3000).then((_arg) => {
        const resp = _arg;
        if (resp.statusCode !== 200) {
            return Promise.resolve(new SageFsStatus(true, false, "no session"));
        }
        else {
            const parsed = JSON.parse(resp.body);
            const h = parsed.healthy;
            const s = parsed.status;
            return Promise.resolve(new SageFsStatus(true, (h == null) ? false : h, (s == null) ? undefined : s));
        }
    }))).catch((_arg_1) => (Promise.resolve(new SageFsStatus(false, undefined, undefined)))))));
}

export function evalCode(code, workingDirectory, c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        const payload = (workingDirectory == null) ? {
            code: code,
            working_directory: "",
        } : {
            code: code,
            working_directory: workingDirectory,
        };
        return PromiseBuilder__Delay_62FBFDE1(promise, () => (httpPost(c, "/exec", JSON.stringify(payload), 30000).then((_arg) => {
            let e;
            const parsed = JSON.parse(_arg.body);
            return Promise.resolve(new EvalResult(parsed.success, parsed.result, (e = parsed.error, (e == null) ? undefined : e)));
        }))).catch((_arg_1) => (Promise.resolve(new EvalResult(false, undefined, toString(_arg_1)))));
    }));
}

export function resetSession(c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => (httpPost(c, "/reset", "{}", 15000).then((_arg) => {
        let m, e;
        const parsed = JSON.parse(_arg.body);
        return Promise.resolve(new EvalResult(parsed.success, (m = parsed.message, (m == null) ? undefined : m), (e = parsed.error, (e == null) ? undefined : e)));
    }))).catch((_arg_1) => (Promise.resolve(new EvalResult(false, undefined, toString(_arg_1))))))));
}

export function hardReset(rebuild, c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => (httpPost(c, "/hard-reset", JSON.stringify({
        rebuild: rebuild,
    }), 60000).then((_arg) => {
        let m, e;
        const parsed = JSON.parse(_arg.body);
        return Promise.resolve(new EvalResult(parsed.success, (m = parsed.message, (m == null) ? undefined : m), (e = parsed.error, (e == null) ? undefined : e)));
    }))).catch((_arg_1) => (Promise.resolve(new EvalResult(false, undefined, toString(_arg_1))))))));
}

export function listSessions(c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => (httpGet(c, "/api/sessions", 5000).then((_arg) => {
        const resp = _arg;
        if (resp.statusCode === 200) {
            const parsed = JSON.parse(resp.body);
            const sessions = parsed.sessions;
            return Promise.resolve(map((s) => {
                let p, e;
                return new SessionInfo(s.id, undefined, s.workingDirectory, s.status, (p = s.projects, (p == null) ? [] : p), (e = s.evalCount, (e == null) ? 0 : e));
            }, sessions));
        }
        else {
            return Promise.resolve([]);
        }
    }))).catch((_arg_1) => {
        console.warn('[SageFs]', "listSessions", _arg_1);
        return Promise.resolve([]);
    }))));
}

export function createSession(projects, workingDirectory, c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => {
        const payload = {
            projects: [projects],
            workingDirectory: workingDirectory,
        };
        return httpPost(c, "/api/sessions/create", JSON.stringify(payload), 30000).then((_arg) => {
            let e;
            const parsed = JSON.parse(_arg.body);
            return Promise.resolve(new EvalResult(parsed.success, parsed.message, (e = parsed.error, (e == null) ? undefined : e)));
        });
    }).catch((_arg_1) => (Promise.resolve(new EvalResult(false, undefined, toString(_arg_1))))))));
}

export function switchSession(sessionId, c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => {
        const payload = {
            sessionId: sessionId,
        };
        return httpPost(c, "/api/sessions/switch", JSON.stringify(payload), 5000).then((_arg) => {
            const parsed = JSON.parse(_arg.body);
            return Promise.resolve(parsed.success);
        });
    }).catch((_arg_1) => {
        console.warn('[SageFs]', "switchSession", _arg_1);
        return Promise.resolve(false);
    }))));
}

export function stopSession(sessionId, c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => {
        const payload = {
            sessionId: sessionId,
        };
        return httpPost(c, "/api/sessions/stop", JSON.stringify(payload), 10000).then((_arg) => {
            const parsed = JSON.parse(_arg.body);
            return Promise.resolve(parsed.success);
        });
    }).catch((_arg_1) => {
        console.warn('[SageFs]', "stopSession", _arg_1);
        return Promise.resolve(false);
    }))));
}

export function getSystemStatus(c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => (httpGet(c, "/api/system/status", 3000).then((_arg) => {
        const resp = _arg;
        if (resp.statusCode === 200) {
            const parsed = JSON.parse(resp.body);
            return Promise.resolve(new SystemStatus(parsed.supervised, parsed.restartCount, parsed.version));
        }
        else {
            return Promise.resolve(undefined);
        }
    }))).catch((_arg_1) => {
        console.warn('[SageFs]', "getSystemStatus", _arg_1);
        return Promise.resolve(undefined);
    }))));
}

export function getHotReloadState(sessionId, c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => (dashHttpGet(c, toText(printf("/api/sessions/%s/hotreload"))(sessionId), 5000).then((_arg) => {
        const resp = _arg;
        if (resp.statusCode === 200) {
            const parsed = JSON.parse(resp.body);
            const rawFiles = parsed.files;
            if (rawFiles == null) {
                return Promise.resolve(new HotReloadState([], 0));
            }
            else {
                const files = choose((f) => {
                    let w;
                    const p = f.path;
                    if (p == null) {
                        return undefined;
                    }
                    else {
                        return new HotReloadFile(p, (w = f.watched, (w == null) ? false : w));
                    }
                }, rawFiles);
                const wc = parsed.watchedCount;
                return Promise.resolve(new HotReloadState(files, (wc == null) ? 0 : wc));
            }
        }
        else {
            return Promise.resolve(undefined);
        }
    }))).catch((_arg_1) => {
        console.warn('[SageFs]', "getHotReloadState", _arg_1);
        return Promise.resolve(undefined);
    }))));
}

export function toggleHotReload(sessionId, path, c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => (dashHttpPost(c, toText(printf("/api/sessions/%s/hotreload/toggle"))(sessionId), JSON.stringify({
        path: path,
    }), 5000).then((_arg) => (Promise.resolve(true))))).catch((_arg_1) => {
        console.warn('[SageFs]', "toggleHotReload", _arg_1);
        return Promise.resolve(false);
    }))));
}

export function watchAllHotReload(sessionId, c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => (dashHttpPost(c, toText(printf("/api/sessions/%s/hotreload/watch-all"))(sessionId), "{}", 5000).then((_arg) => (Promise.resolve(true))))).catch((_arg_1) => {
        console.warn('[SageFs]', "watchAllHotReload", _arg_1);
        return Promise.resolve(false);
    }))));
}

export function unwatchAllHotReload(sessionId, c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => (dashHttpPost(c, toText(printf("/api/sessions/%s/hotreload/unwatch-all"))(sessionId), "{}", 5000).then((_arg) => (Promise.resolve(true))))).catch((_arg_1) => {
        console.warn('[SageFs]', "unwatchAllHotReload", _arg_1);
        return Promise.resolve(false);
    }))));
}

export function watchDirectoryHotReload(sessionId, directory, c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => (dashHttpPost(c, toText(printf("/api/sessions/%s/hotreload/watch-directory"))(sessionId), JSON.stringify({
        directory: directory,
    }), 5000).then((_arg) => (Promise.resolve(true))))).catch((_arg_1) => {
        console.warn('[SageFs]', "watchDirectoryHotReload", _arg_1);
        return Promise.resolve(false);
    }))));
}

export function unwatchDirectoryHotReload(sessionId, directory, c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => (dashHttpPost(c, toText(printf("/api/sessions/%s/hotreload/unwatch-directory"))(sessionId), JSON.stringify({
        directory: directory,
    }), 5000).then((_arg) => (Promise.resolve(true))))).catch((_arg_1) => {
        console.warn('[SageFs]', "unwatchDirectoryHotReload", _arg_1);
        return Promise.resolve(false);
    }))));
}

export function getWarmupContext(sessionId, c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => (dashHttpGet(c, toText(printf("/api/sessions/%s/warmup-context"))(sessionId), 5000).then((_arg) => {
        const resp = _arg;
        if (resp.statusCode === 200) {
            const parsed = JSON.parse(resp.body);
            const assemblies = map((a) => (new LoadedAssemblyInfo(a.Name, a.Path, a.NamespaceCount, a.ModuleCount)), parsed.AssembliesLoaded);
            const opened = map((b) => (new OpenedBindingInfo(b.Name, b.IsModule, b.Source)), parsed.NamespacesOpened);
            const failed = map((f) => f, parsed.FailedOpens);
            return Promise.resolve(new WarmupContextInfo(parsed.SourceFilesScanned, assemblies, opened, failed, parsed.WarmupDurationMs));
        }
        else {
            return Promise.resolve(undefined);
        }
    }))).catch((_arg_1) => {
        console.warn('[SageFs]', "getWarmupContext", _arg_1);
        return Promise.resolve(undefined);
    }))));
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
                const parsed = JSON.parse(resp.body);
                const items = parsed.completions;
                return Promise.resolve(map((item) => (new CompletionResult(item.label, item.kind, item.insertText)), items));
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
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => {
        const payload = {
            category: "",
            pattern: pattern,
        };
        return httpPost(c, "/api/live-testing/run", JSON.stringify(payload), 60000).then((_arg) => {
            let m, e;
            const parsed = JSON.parse(_arg.body);
            return Promise.resolve(new EvalResult(parsed.success, (m = parsed.message, (m == null) ? undefined : m), (e = parsed.error, (e == null) ? undefined : e)));
        });
    }).catch((_arg_1) => (Promise.resolve(new EvalResult(false, undefined, toString(_arg_1))))))));
}

export function enableLiveTesting(c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => (httpPost(c, "/api/live-testing/enable", "{}", 5000).then((_arg) => {
        let m, e;
        const parsed = JSON.parse(_arg.body);
        return Promise.resolve(new EvalResult(parsed.success, (m = parsed.message, (m == null) ? undefined : m), (e = parsed.error, (e == null) ? undefined : e)));
    }))).catch((_arg_1) => (Promise.resolve(new EvalResult(false, undefined, toString(_arg_1))))))));
}

export function disableLiveTesting(c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => (httpPost(c, "/api/live-testing/disable", "{}", 5000).then((_arg) => {
        let m, e;
        const parsed = JSON.parse(_arg.body);
        return Promise.resolve(new EvalResult(parsed.success, (m = parsed.message, (m == null) ? undefined : m), (e = parsed.error, (e == null) ? undefined : e)));
    }))).catch((_arg_1) => (Promise.resolve(new EvalResult(false, undefined, toString(_arg_1))))))));
}

export function setRunPolicy(category, policy, c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => {
        const payload = {
            category: category,
            policy: policy,
        };
        return httpPost(c, "/api/live-testing/policy", JSON.stringify(payload), 5000).then((_arg) => {
            let m, e;
            const parsed = JSON.parse(_arg.body);
            return Promise.resolve(new EvalResult(parsed.success, (m = parsed.message, (m == null) ? undefined : m), (e = parsed.error, (e == null) ? undefined : e)));
        });
    }).catch((_arg_1) => (Promise.resolve(new EvalResult(false, undefined, toString(_arg_1))))))));
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
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => (httpGet(c, toText(printf("/api/recent-events?count=%d"))(count), 10000).then((_arg) => {
        const resp = _arg;
        return (resp.statusCode === 200) ? (Promise.resolve(resp.body)) : (Promise.resolve(undefined));
    }))).catch((_arg_1) => {
        console.warn('[SageFs]', "getRecentEvents", _arg_1);
        return Promise.resolve(undefined);
    }))));
}

export function getDependencyGraph(symbol, c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => {
        let path;
        if (symbol === "") {
            path = "/api/dependency-graph";
        }
        else {
            const arg = encodeURIComponent(symbol);
            path = toText(printf("/api/dependency-graph?symbol=%s"))(arg);
        }
        return httpGet(c, path, 10000).then((_arg) => {
            const resp = _arg;
            return (resp.statusCode === 200) ? (Promise.resolve(resp.body)) : (Promise.resolve(undefined));
        });
    }).catch((_arg_1) => {
        console.warn('[SageFs]', "getDependencyGraph", _arg_1);
        return Promise.resolve(undefined);
    }))));
}

