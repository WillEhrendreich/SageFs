// LD_PRELOAD canary for lost file descriptors. See docs/fd-loss-in-the-default-tier.md.
//
//   gcc -O1 -g -shared -fPIC -fno-omit-frame-pointer -o fdshim.so fdshim.c -ldl
//   FDSHIM_LOG=/path/to/log LD_PRELOAD=$PWD/fdshim.so DOTNET_PerfMapEnabled=3 DOTNET_PerfMapJitDumpPath=/path/to/perf \
//     DOTNET_ReadyToRun=0 dotnet SageFs.Tests.dll --summary
//   docs/fd-canary/resolve.sh /path/to/log /path/to/perf
//
// It reports, with the thread, the time and a frame-pointer stack walk:
//   EBADF-CLOSE  a close (or closedir, fclose) that failed with EBADF: a double close, or a close of a number nobody owns.
//   VICTIM-<call> a read, write, pread, pwrite, lseek, fstat, fsync or ftruncate that was told its descriptor is not open.
//   LOWFD-OPEN / LOWFD-CLOSE  an open handed descriptor 0, 1 or 2, or a close of one: the process lost a standard stream.
//   DUP2 / DUP3 / CLOSE_RANGE  any call that replaces or sweeps descriptors above 2.
// Each hit also prints who last opened and last closed that descriptor number. A PROC line per exec names the pid.
// A double close shows as EBADF-CLOSE when nobody reopened the number in between, and as a VICTIM (the number's new
// owner) plus a later EBADF-CLOSE when somebody did.
#define _GNU_SOURCE
#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <pthread.h>
#include <stdarg.h>
#include <stdlib.h>
#include <stdio.h>
#include <stdint.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/syscall.h>
#include <time.h>
#include <unistd.h>

#define MAXFD 65536
static int (*real_close)(int);
static int logfd = -1;
static struct ev { int tid; long long ns; const char *what; void *pc[6]; } last_open[MAXFD], last_close[MAXFD];

static long long now_ns(void) { struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return t.tv_sec * 1000000000LL + t.tv_nsec; }
static int gettid_(void) { return (int)syscall(SYS_gettid); }

__attribute__((constructor)) static void init(void) {
  real_close = dlsym(RTLD_NEXT, "close");
  const char *p = getenv("FDSHIM_LOG");
  if (p) {
    int fd = open(p, O_WRONLY | O_CREAT | O_APPEND | O_CLOEXEC, 0644);
    if (fd >= 0) { logfd = fcntl(fd, F_DUPFD_CLOEXEC, 900); real_close(fd); }
  }
  if (logfd >= 0) {
    char exe[512] = {0}; ssize_t n = readlink("/proc/self/exe", exe, sizeof exe - 1); (void)n;
    char cmd[300] = {0}; int cf = open("/proc/self/cmdline", O_RDONLY); if (cf >= 0) { ssize_t m = read(cf, cmd, sizeof cmd - 1); for (ssize_t i = 0; i < m; i++) if (cmd[i] == 0) cmd[i] = ' '; real_close(cf); }
    char line[1024]; int len = snprintf(line, sizeof line, "PROC pid=%d ppid=%d exe=%s cmd=%s\n", getpid(), getppid(), exe, cmd);
    ssize_t w = write(logfd, line, len); (void)w;
  }
}

static int walk(void **out, int max) {
  void **fp = (void **)__builtin_frame_address(0);
  int n = 0;
  while (fp && n < max && ((uintptr_t)fp & 7) == 0 && (uintptr_t)fp > 4096) {
    void *ret = fp[1];
    void **next = (void **)fp[0];
    if (!ret) break;
    out[n++] = ret;
    if (next <= fp || (uintptr_t)next - (uintptr_t)fp > (1u << 24)) break;
    fp = next;
  }
  return n;
}

static void say(const char *fmt, ...) {
  char buf[2048]; va_list ap; va_start(ap, fmt);
  int n = vsnprintf(buf, sizeof buf, fmt, ap); va_end(ap);
  if (logfd >= 0 && n > 0) { ssize_t r = write(logfd, buf, n < (int)sizeof buf ? n : (int)sizeof buf - 1); (void)r; }
}

// A frame as `pc` when no loaded module owns it (JIT code, resolved offline from the runtime's perf map), else as
// `pc=module+0xoffset` and the nearest exported symbol.
static void say_stack(void **pc, int n) {
  for (int i = 0; i < n; i++) {
    Dl_info d;
    if (dladdr(pc[i], &d) && d.dli_fname) {
      const char *base = strrchr(d.dli_fname, '/'); base = base ? base + 1 : d.dli_fname;
      say(" %p=%s+0x%lx", pc[i], base, (unsigned long)((char *)pc[i] - (char *)d.dli_fbase));
      if (d.dli_sname) say("(%s)", d.dli_sname);
    } else say(" %p", pc[i]);
  }
}

static void lowfd(const char *kind, int fd, const char *what) {
  void *pc[16]; int n = walk(pc, 16);
  say("%s pid=%d fd=%d tid=%d via %s stack:", kind, getpid(), fd, gettid_(), what);
  say_stack(pc, n);
  say("\n");
}
static void note(struct ev *tab, int fd, const char *what) {
  if (fd >= 0 && fd <= 2 && tab == last_open) lowfd("LOWFD-OPEN", fd, what);
  if (fd >= 0 && fd <= 2 && tab == last_close) lowfd("LOWFD-CLOSE", fd, what);
  if (fd < 0 || fd >= MAXFD) return;
  void *pc[6]; int n = walk(pc, 6);
  tab[fd].tid = gettid_(); tab[fd].ns = now_ns(); tab[fd].what = what;
  for (int i = 0; i < 6; i++) tab[fd].pc[i] = i < n ? pc[i] : 0;
}

static void show(const char *label, struct ev *e) {
  if (!e->what) { say("  %s: none\n", label); return; }
  say("  %s: tid=%d t=%lld ns %s pcs=%p %p %p %p %p %p\n", label, e->tid, e->ns, e->what, e->pc[0], e->pc[1], e->pc[2], e->pc[3], e->pc[4], e->pc[5]);
}

int close(int fd) {
  if (!real_close) real_close = dlsym(RTLD_NEXT, "close");
  int r = real_close(fd);
  int e = errno;
  if (r < 0 && e == EBADF && fd > 2) {
    void *pc[24]; int n = walk(pc, 24);
    say("EBADF-CLOSE pid=%d fd=%d tid=%d t=%lld ns stack:", getpid(), fd, gettid_(), now_ns());
    say_stack(pc, n);
    say("\n");
    show("last open ", &last_open[fd < MAXFD ? fd : 0]);
    show("last close", &last_close[fd < MAXFD ? fd : 0]);
  } else if (r == 0) {
    note(last_close, fd, "close");
  }
  errno = e;
  return r;
}

#define OPENLIKE(ret, name, proto, call) \
  ret name proto { static ret (*real) proto; if (!real) real = dlsym(RTLD_NEXT, #name); ret r = real call; if ((long)r >= 0) note(last_open, (int)r, #name); return r; }

int open(const char *path, int flags, ...) {
  static int (*real)(const char *, int, ...); if (!real) real = dlsym(RTLD_NEXT, "open");
  mode_t m = 0; if (flags & (O_CREAT | O_TMPFILE)) { va_list ap; va_start(ap, flags); m = va_arg(ap, mode_t); va_end(ap); }
  int r = real(path, flags, m); if (r >= 0) note(last_open, r, "open"); return r; }
int open64(const char *path, int flags, ...) {
  static int (*real)(const char *, int, ...); if (!real) real = dlsym(RTLD_NEXT, "open64");
  mode_t m = 0; if (flags & (O_CREAT | O_TMPFILE)) { va_list ap; va_start(ap, flags); m = va_arg(ap, mode_t); va_end(ap); }
  int r = real(path, flags, m); if (r >= 0) note(last_open, r, "open64"); return r; }
int pipe2(int fds[2], int flags) {
  static int (*real)(int[2], int); if (!real) real = dlsym(RTLD_NEXT, "pipe2");
  int r = real(fds, flags); if (r == 0) { note(last_open, fds[0], "pipe2r"); note(last_open, fds[1], "pipe2w"); } return r; }
int pipe(int fds[2]) {
  static int (*real)(int[2]); if (!real) real = dlsym(RTLD_NEXT, "pipe");
  int r = real(fds); if (r == 0) { note(last_open, fds[0], "pipe-r"); note(last_open, fds[1], "pipe-w"); } return r; }
OPENLIKE(int, dup, (int a), (a))
OPENLIKE(int, socket, (int a, int b, int c), (a, b, c))
OPENLIKE(int, accept4, (int a, struct sockaddr *b, socklen_t *c, int d), (a, b, c, d))
OPENLIKE(int, epoll_create1, (int a), (a))
OPENLIKE(int, eventfd, (unsigned a, int b), (a, b))
OPENLIKE(int, inotify_init1, (int a), (a))

// Calls that replace or sweep descriptors are reported every time, with a stack.
int dup2(int oldfd, int newfd) {
  static int (*real)(int, int); if (!real) real = dlsym(RTLD_NEXT, "dup2");
  int r = real(oldfd, newfd);
  if (newfd > 2) { void *pc[16]; int n = walk(pc, 16); say("DUP2 old=%d new=%d r=%d tid=%d stack:", oldfd, newfd, r, gettid_()); say_stack(pc, n); say("\n"); }
  return r; }
int dup3(int oldfd, int newfd, int flags) {
  static int (*real)(int, int, int); if (!real) real = dlsym(RTLD_NEXT, "dup3");
  int r = real(oldfd, newfd, flags);
  if (newfd > 2) { void *pc[16]; int n = walk(pc, 16); say("DUP3 old=%d new=%d r=%d tid=%d stack:", oldfd, newfd, r, gettid_()); say_stack(pc, n); say("\n"); }
  return r; }
int close_range(unsigned first, unsigned last, int flags) {
  static int (*real)(unsigned, unsigned, int); if (!real) real = dlsym(RTLD_NEXT, "close_range");
  int r = real ? real(first, last, flags) : (int)syscall(436, first, last, flags);
  void *pc[16]; int n = walk(pc, 16); say("CLOSE_RANGE %u %u flags=%d r=%d tid=%d stack:", first, last, flags, r, gettid_()); say_stack(pc, n); say("\n");
  return r; }

// A call that uses a descriptor and is told it is not open. The victim side of the same bug: who closed that number last?
static void victim(const char *call, int fd) {
  if (fd <= 2) return;
  void *pc[24]; int n = walk(pc, 24);
  say("VICTIM-%s pid=%d fd=%d tid=%d t=%lld ns stack:", call, getpid(), fd, gettid_(), now_ns());
  say_stack(pc, n);
  say("\n");
  show("last open ", &last_open[fd < MAXFD ? fd : 0]);
  show("last close", &last_close[fd < MAXFD ? fd : 0]);
}
#define VICTIM3(ret, name, p1, p2, p3) \
  ret name(p1 a, p2 b, p3 c) { static ret (*real)(p1, p2, p3); if (!real) real = dlsym(RTLD_NEXT, #name); ret r = real(a, b, c); int e = errno; if (r < 0 && e == EBADF) victim(#name, (int)a); errno = e; return r; }
VICTIM3(ssize_t, read, int, void *, size_t)
VICTIM3(ssize_t, write, int, const void *, size_t)
VICTIM3(off_t, lseek, int, off_t, int)
VICTIM3(off64_t, lseek64, int, off64_t, int)
#define VICTIM4(ret, name, p1, p2, p3, p4) \
  ret name(p1 a, p2 b, p3 c, p4 d) { static ret (*real)(p1, p2, p3, p4); if (!real) real = dlsym(RTLD_NEXT, #name); ret r = real(a, b, c, d); int e = errno; if (r < 0 && e == EBADF) victim(#name, (int)a); errno = e; return r; }
VICTIM4(ssize_t, pread, int, void *, size_t, off_t)
VICTIM4(ssize_t, pread64, int, void *, size_t, off64_t)
VICTIM4(ssize_t, pwrite, int, const void *, size_t, off_t)
VICTIM4(ssize_t, pwrite64, int, const void *, size_t, off64_t)
struct stat; struct stat64;
int fstat(int fd, struct stat *st) { static int (*real)(int, struct stat *); if (!real) real = dlsym(RTLD_NEXT, "fstat"); int r = real(fd, st); int e = errno; if (r < 0 && e == EBADF) victim("fstat", fd); errno = e; return r; }
int fstat64(int fd, struct stat64 *st) { static int (*real)(int, struct stat64 *); if (!real) real = dlsym(RTLD_NEXT, "fstat64"); int r = real(fd, st); int e = errno; if (r < 0 && e == EBADF) victim("fstat64", fd); errno = e; return r; }
int fsync(int fd) { static int (*real)(int); if (!real) real = dlsym(RTLD_NEXT, "fsync"); int r = real(fd); int e = errno; if (r < 0 && e == EBADF) victim("fsync", fd); errno = e; return r; }
int ftruncate(int fd, off_t l) { static int (*real)(int, off_t); if (!real) real = dlsym(RTLD_NEXT, "ftruncate"); int r = real(fd, l); int e = errno; if (r < 0 && e == EBADF) victim("ftruncate", fd); errno = e; return r; }
int ftruncate64(int fd, off64_t l) { static int (*real)(int, off64_t); if (!real) real = dlsym(RTLD_NEXT, "ftruncate64"); int r = real(fd, l); int e = errno; if (r < 0 && e == EBADF) victim("ftruncate64", fd); errno = e; return r; }

// glibc's own closes (closedir, fclose) bypass the interposed close(). Wrap them so a double closedir or fclose is seen.
#include <dirent.h>
static void badclose(const char *call, int fd, int e) {
  void *pc[24]; int n = walk(pc, 24);
  say("EBADF-CLOSE pid=%d fd=%d tid=%d t=%lld ns via %s errno=%d stack:", getpid(), fd, gettid_(), now_ns(), call, e);
  say_stack(pc, n);
  say("\n");
  show("last open ", &last_open[fd >= 0 && fd < MAXFD ? fd : 0]);
  show("last close", &last_close[fd >= 0 && fd < MAXFD ? fd : 0]);
}
DIR *opendir(const char *name) { static DIR *(*real)(const char *); if (!real) real = dlsym(RTLD_NEXT, "opendir"); DIR *d = real(name); if (d) note(last_open, dirfd(d), "opendir"); return d; }
DIR *fdopendir(int fd) { static DIR *(*real)(int); if (!real) real = dlsym(RTLD_NEXT, "fdopendir"); DIR *d = real(fd); return d; }
int closedir(DIR *d) {
  static int (*real)(DIR *); if (!real) real = dlsym(RTLD_NEXT, "closedir");
  int fd = d ? dirfd(d) : -1;
  int r = real(d); int e = errno;
  if (r < 0 && e == EBADF) badclose("closedir", fd, e); else if (r == 0) note(last_close, fd, "closedir");
  errno = e; return r; }
#include <stdio.h>
FILE *fopen(const char *p, const char *m) { static FILE *(*real)(const char *, const char *); if (!real) real = dlsym(RTLD_NEXT, "fopen"); FILE *f = real(p, m); if (f) note(last_open, fileno(f), "fopen"); return f; }
FILE *fopen64(const char *p, const char *m) { static FILE *(*real)(const char *, const char *); if (!real) real = dlsym(RTLD_NEXT, "fopen64"); FILE *f = real(p, m); if (f) note(last_open, fileno(f), "fopen64"); return f; }
int fclose(FILE *f) {
  static int (*real)(FILE *); if (!real) real = dlsym(RTLD_NEXT, "fclose");
  int fd = f ? fileno(f) : -1;
  int r = real(f); int e = errno;
  if (r != 0 && e == EBADF) badclose("fclose", fd, e); else if (r == 0) note(last_close, fd, "fclose");
  errno = e; return r; }
