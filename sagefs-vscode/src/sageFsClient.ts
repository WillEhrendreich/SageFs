import * as http from "http";

export interface EvalResult {
  success: boolean;
  result?: string;
  error?: string;
}

export interface SageFsStatus {
  connected: boolean;
  healthy?: boolean;
  status?: string;
}

/**
 * HTTP client for SageFs daemon REST API.
 * Uses the direct /exec, /health, /reset, /hard-reset endpoints.
 */
export class SageFsClient {
  private mcpPort: number;
  private dashboardPort: number;

  constructor(mcpPort: number = 37749, dashboardPort: number = 37750) {
    this.mcpPort = mcpPort;
    this.dashboardPort = dashboardPort;
  }

  get baseUrl(): string {
    return `http://localhost:${this.mcpPort}`;
  }

  get dashboardUrl(): string {
    return `http://localhost:${this.dashboardPort}/dashboard`;
  }

  updatePorts(mcpPort: number, dashboardPort: number): void {
    this.mcpPort = mcpPort;
    this.dashboardPort = dashboardPort;
  }

  async isRunning(): Promise<boolean> {
    try {
      // Any response (even 500) means the daemon is alive.
      // /health may return 500 if no session matches, but the daemon IS running.
      const resp = await this.httpGet("/health", 3000);
      return resp.statusCode > 0;
    } catch {
      return false;
    }
  }

  async getStatus(): Promise<SageFsStatus> {
    try {
      const resp = await this.httpGet("/health", 3000);
      if (resp.statusCode !== 200) {
        // Daemon is alive but no session matched — still connected
        return { connected: true, healthy: false, status: "no session" };
      }
      const parsed = JSON.parse(resp.body);
      return {
        connected: true,
        healthy: parsed.healthy,
        status: parsed.status,
      };
    } catch {
      return { connected: false };
    }
  }

  /** POST /exec — evaluate F# code */
  async evalCode(code: string, workingDirectory?: string): Promise<EvalResult> {
    const payload: Record<string, string> = { code };
    if (workingDirectory) {
      payload.working_directory = workingDirectory;
    }
    try {
      const resp = await this.httpPost("/exec", JSON.stringify(payload), 30000);
      const parsed = JSON.parse(resp.body);
      return {
        success: parsed.success ?? false,
        result: parsed.result,
        error: parsed.error,
      };
    } catch (err) {
      return { success: false, error: String(err) };
    }
  }

  /** POST /reset — soft reset FSI session */
  async resetSession(): Promise<EvalResult> {
    try {
      const resp = await this.httpPost("/reset", "{}", 15000);
      const parsed = JSON.parse(resp.body);
      return {
        success: parsed.success ?? false,
        result: parsed.message,
        error: parsed.error,
      };
    } catch (err) {
      return { success: false, error: String(err) };
    }
  }

  /** POST /hard-reset — hard reset with rebuild */
  async hardReset(rebuild: boolean): Promise<EvalResult> {
    try {
      const resp = await this.httpPost(
        "/hard-reset",
        JSON.stringify({ rebuild }),
        60000
      );
      const parsed = JSON.parse(resp.body);
      return {
        success: parsed.success ?? false,
        result: parsed.message,
        error: parsed.error,
      };
    } catch (err) {
      return { success: false, error: String(err) };
    }
  }

  private httpGet(
    path: string,
    timeout: number
  ): Promise<{ statusCode: number; body: string }> {
    return new Promise((resolve, reject) => {
      const req = http.get(`${this.baseUrl}${path}`, { timeout }, (res) => {
        let data = "";
        res.on("data", (chunk) => (data += chunk));
        res.on("end", () =>
          resolve({ statusCode: res.statusCode ?? 0, body: data })
        );
      });
      req.on("error", reject);
      req.on("timeout", () => {
        req.destroy();
        reject(new Error("timeout"));
      });
    });
  }

  private httpPost(
    path: string,
    body: string,
    timeout: number
  ): Promise<{ statusCode: number; body: string }> {
    return new Promise((resolve, reject) => {
      const url = new URL(`${this.baseUrl}${path}`);
      const req = http.request(
        {
          hostname: url.hostname,
          port: url.port,
          path: url.pathname,
          method: "POST",
          headers: {
            "Content-Type": "application/json",
            "Content-Length": Buffer.byteLength(body),
          },
          timeout,
        },
        (res) => {
          let data = "";
          res.on("data", (chunk) => (data += chunk));
          res.on("end", () =>
            resolve({ statusCode: res.statusCode ?? 0, body: data })
          );
        }
      );
      req.on("error", reject);
      req.on("timeout", () => {
        req.destroy();
        reject(new Error("timeout"));
      });
      req.write(body);
      req.end();
    });
  }
}
