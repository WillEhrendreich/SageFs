import subprocess
import sys
from pathlib import Path


def main() -> int:
    if len(sys.argv) != 4:
        print("usage: launch_daemon_detached.py <exe> <workdir> <mcp_port>")
        return 1

    exe = sys.argv[1]
    workdir = Path(sys.argv[2])
    mcp_port = sys.argv[3]
    stdout_path = workdir / f"daemon-{mcp_port}.out.log"
    stderr_path = workdir / f"daemon-{mcp_port}.err.log"

    with open(stdout_path, "ab") as stdout, open(stderr_path, "ab") as stderr:
        proc = subprocess.Popen(
            [exe, "daemon", "--mcp-port", mcp_port],
            cwd=str(workdir),
            stdout=stdout,
            stderr=stderr,
            creationflags=subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP,
        )

    print(proc.pid)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
