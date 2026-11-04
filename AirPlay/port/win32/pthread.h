/*
 * The pthreads UxPlay uses, on Windows threads: SRW locks and condition variables (both work
 * without initialisation beyond zeroing, so the *_init calls are simple).
 *
 * openbox is free software: GPL-3.0-or-later.
 */

#ifndef OPENBOX_WIN32_PTHREAD_H
#define OPENBOX_WIN32_PTHREAD_H

#include <winsock2.h>
#include <windows.h>
#include <process.h>
#include <time.h>
#include <errno.h>

typedef HANDLE pthread_t;
typedef SRWLOCK pthread_mutex_t;
typedef CONDITION_VARIABLE pthread_cond_t;
typedef void pthread_attr_t;
typedef void pthread_mutexattr_t;
typedef void pthread_condattr_t;

typedef struct {
    void *(*func)(void *);
    void *arg;
} openbox_thread_start_t;

static unsigned __stdcall openbox_thread_main(void *start) {
    openbox_thread_start_t run = *(openbox_thread_start_t *) start;
    free(start);
    run.func(run.arg);
    return 0;
}

static __inline int pthread_create(pthread_t *thread, const pthread_attr_t *attr, void *(*func)(void *), void *arg) {
    openbox_thread_start_t *start = (openbox_thread_start_t *) malloc(sizeof(openbox_thread_start_t));
    (void) attr;
    if (!start) {
        return ENOMEM;
    }
    start->func = func;
    start->arg = arg;
    *thread = (HANDLE) _beginthreadex(NULL, 0, openbox_thread_main, start, 0, NULL);
    if (!*thread) {
        free(start);
        return EAGAIN;
    }
    return 0;
}

static __inline int pthread_join(pthread_t thread, void **result) {
    if (result) {
        *result = NULL;
    }
    WaitForSingleObject(thread, INFINITE);
    CloseHandle(thread);
    return 0;
}

static __inline int pthread_mutex_init(pthread_mutex_t *mutex, const pthread_mutexattr_t *attr) {
    (void) attr;
    InitializeSRWLock(mutex);
    return 0;
}
static __inline int pthread_mutex_lock(pthread_mutex_t *mutex) { AcquireSRWLockExclusive(mutex); return 0; }
static __inline int pthread_mutex_unlock(pthread_mutex_t *mutex) { ReleaseSRWLockExclusive(mutex); return 0; }
static __inline int pthread_mutex_destroy(pthread_mutex_t *mutex) { (void) mutex; return 0; }

static __inline int pthread_cond_init(pthread_cond_t *cond, const pthread_condattr_t *attr) {
    (void) attr;
    InitializeConditionVariable(cond);
    return 0;
}
static __inline int pthread_cond_signal(pthread_cond_t *cond) { WakeConditionVariable(cond); return 0; }
static __inline int pthread_cond_broadcast(pthread_cond_t *cond) { WakeAllConditionVariable(cond); return 0; }
static __inline int pthread_cond_destroy(pthread_cond_t *cond) { (void) cond; return 0; }
static __inline int pthread_cond_wait(pthread_cond_t *cond, pthread_mutex_t *mutex) {
    SleepConditionVariableSRW(cond, mutex, INFINITE, 0);
    return 0;
}

#ifndef CLOCK_REALTIME
#define CLOCK_REALTIME 0
#endif
#ifndef CLOCK_MONOTONIC
#define CLOCK_MONOTONIC 1
#endif

static __inline int clock_gettime(int clock, struct timespec *time) {
    (void) clock;
    timespec_get(time, TIME_UTC);
    return 0;
}

/* Waits until an absolute CLOCK_REALTIME time. */
static __inline int pthread_cond_timedwait(pthread_cond_t *cond, pthread_mutex_t *mutex, const struct timespec *until) {
    struct timespec now;
    timespec_get(&now, TIME_UTC);
    long long ms = (long long) (until->tv_sec - now.tv_sec) * 1000 + (until->tv_nsec - now.tv_nsec) / 1000000;
    if (ms < 0) {
        ms = 0;
    }
    return SleepConditionVariableSRW(cond, mutex, (DWORD) ms, 0) ? 0 : ETIMEDOUT;
}

static __inline int usleep(unsigned int microseconds) {
    Sleep(microseconds / 1000);
    return 0;
}

#endif
