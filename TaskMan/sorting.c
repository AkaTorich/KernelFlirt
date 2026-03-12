/* sorting.c - Sorting functions for all list views */
#include "taskman.h"

/* ========================================================================= */
/* PROCESS COMPARATORS                                                       */
/* ========================================================================= */

static int cmp_proc_name(const void *a, const void *b) {
    int r = lstrcmpiW(((const ProcessInfo*)a)->exeName, ((const ProcessInfo*)b)->exeName);
    return g_procSortAsc[PROC_COL_NAME] ? r : -r;
}
static int cmp_proc_pid(const void *a, const void *b) {
    DWORD pa = ((const ProcessInfo*)a)->pid, pb = ((const ProcessInfo*)b)->pid;
    int r = (pa > pb) ? 1 : (pa < pb) ? -1 : 0;
    return g_procSortAsc[PROC_COL_PID] ? r : -r;
}
static int cmp_proc_mem(const void *a, const void *b) {
    SIZE_T ma = ((const ProcessInfo*)a)->workingSetKB, mb = ((const ProcessInfo*)b)->workingSetKB;
    int r = (ma > mb) ? 1 : (ma < mb) ? -1 : 0;
    return g_procSortAsc[PROC_COL_MEMORY] ? r : -r;
}
static int cmp_proc_cpu(const void *a, const void *b) {
    double ca = ((const ProcessInfo*)a)->cpuUsage, cb = ((const ProcessInfo*)b)->cpuUsage;
    int r = (ca > cb) ? 1 : (ca < cb) ? -1 : 0;
    return g_procSortAsc[PROC_COL_CPU] ? r : -r;
}
static int cmp_proc_path(const void *a, const void *b) {
    int r = lstrcmpiW(((const ProcessInfo*)a)->fullPath, ((const ProcessInfo*)b)->fullPath);
    return g_procSortAsc[PROC_COL_PATH] ? r : -r;
}
static int cmp_proc_network(const void *a, const void *b) {
    int na = ((const ProcessInfo*)a)->tcpConnections + ((const ProcessInfo*)a)->udpConnections;
    int nb = ((const ProcessInfo*)b)->tcpConnections + ((const ProcessInfo*)b)->udpConnections;
    int r = (na > nb) ? 1 : (na < nb) ? -1 : 0;
    return g_procSortAsc[PROC_COL_NETWORK] ? r : -r;
}
static int cmp_proc_threads(const void *a, const void *b) {
    DWORD ta = ((const ProcessInfo*)a)->threadCount, tb = ((const ProcessInfo*)b)->threadCount;
    int r = (ta > tb) ? 1 : (ta < tb) ? -1 : 0;
    return g_procSortAsc[PROC_COL_THREADS] ? r : -r;
}
static int cmp_proc_handles(const void *a, const void *b) {
    DWORD ha = ((const ProcessInfo*)a)->handleCount, hb = ((const ProcessInfo*)b)->handleCount;
    int r = (ha > hb) ? 1 : (ha < hb) ? -1 : 0;
    return g_procSortAsc[PROC_COL_HANDLES] ? r : -r;
}
static int cmp_proc_gpu(const void *a, const void *b) {
    double ga = ((const ProcessInfo*)a)->gpuUsage, gb = ((const ProcessInfo*)b)->gpuUsage;
    int r = (ga > gb) ? 1 : (ga < gb) ? -1 : 0;
    return g_procSortAsc[PROC_COL_GPU] ? r : -r;
}
static int cmp_proc_diskread(const void *a, const void *b) {
    double ra = ((const ProcessInfo*)a)->ioReadRate, rb = ((const ProcessInfo*)b)->ioReadRate;
    int r = (ra > rb) ? 1 : (ra < rb) ? -1 : 0;
    return g_procSortAsc[PROC_COL_DISK_READ] ? r : -r;
}
static int cmp_proc_diskwrite(const void *a, const void *b) {
    double wa = ((const ProcessInfo*)a)->ioWriteRate, wb = ((const ProcessInfo*)b)->ioWriteRate;
    int r = (wa > wb) ? 1 : (wa < wb) ? -1 : 0;
    return g_procSortAsc[PROC_COL_DISK_WRITE] ? r : -r;
}

/* ========================================================================= */
/* AUTORUN COMPARATORS                                                       */
/* ========================================================================= */

static int cmp_ar_enabled(const void *a, const void *b) {
    BOOL ea = ((const AutorunInfo*)a)->enabled, eb = ((const AutorunInfo*)b)->enabled;
    int r = (ea == eb) ? 0 : ea ? -1 : 1;
    return g_arSortAsc[AR_COL_ENABLED] ? r : -r;
}
static int cmp_ar_name(const void *a, const void *b) {
    int r = lstrcmpiW(((const AutorunInfo*)a)->name, ((const AutorunInfo*)b)->name);
    return g_arSortAsc[AR_COL_NAME] ? r : -r;
}
static int cmp_ar_desc(const void *a, const void *b) {
    int r = lstrcmpiW(((const AutorunInfo*)a)->description, ((const AutorunInfo*)b)->description);
    return g_arSortAsc[AR_COL_DESCRIPTION] ? r : -r;
}
static int cmp_ar_company(const void *a, const void *b) {
    int r = lstrcmpiW(((const AutorunInfo*)a)->company, ((const AutorunInfo*)b)->company);
    return g_arSortAsc[AR_COL_COMPANY] ? r : -r;
}
static int cmp_ar_path(const void *a, const void *b) {
    int r = lstrcmpiW(((const AutorunInfo*)a)->fullPath, ((const AutorunInfo*)b)->fullPath);
    return g_arSortAsc[AR_COL_PATH] ? r : -r;
}
static int cmp_ar_source(const void *a, const void *b) {
    int r = lstrcmpiW(((const AutorunInfo*)a)->sourceDetails, ((const AutorunInfo*)b)->sourceDetails);
    return g_arSortAsc[AR_COL_SOURCE] ? r : -r;
}
static int cmp_ar_verified(const void *a, const void *b) {
    BOOL va = ((const AutorunInfo*)a)->verified, vb = ((const AutorunInfo*)b)->verified;
    int r = (va == vb) ? 0 : va ? -1 : 1;
    return g_arSortAsc[AR_COL_VERIFIED] ? r : -r;
}

/* ========================================================================= */
/* TASK SCHEDULER COMPARATORS                                                */
/* ========================================================================= */

static int cmp_ts_name(const void *a, const void *b) {
    int r = lstrcmpiW(((const ScheduledTaskInfo*)a)->name, ((const ScheduledTaskInfo*)b)->name);
    return g_tsSortAsc[TS_COL_NAME] ? r : -r;
}
static int cmp_ts_status(const void *a, const void *b) {
    int sa = ((const ScheduledTaskInfo*)a)->state, sb = ((const ScheduledTaskInfo*)b)->state;
    int r = (sa > sb) ? 1 : (sa < sb) ? -1 : 0;
    return g_tsSortAsc[TS_COL_STATUS] ? r : -r;
}
static int cmp_ts_trigger(const void *a, const void *b) {
    int r = lstrcmpiW(((const ScheduledTaskInfo*)a)->triggerDescription, ((const ScheduledTaskInfo*)b)->triggerDescription);
    return g_tsSortAsc[TS_COL_TRIGGER] ? r : -r;
}
static int cmp_ts_lastrun(const void *a, const void *b) {
    double la = ((const ScheduledTaskInfo*)a)->lastRunTime, lb = ((const ScheduledTaskInfo*)b)->lastRunTime;
    int r = (la > lb) ? 1 : (la < lb) ? -1 : 0;
    return g_tsSortAsc[TS_COL_LAST_RUN] ? r : -r;
}
static int cmp_ts_nextrun(const void *a, const void *b) {
    double na = ((const ScheduledTaskInfo*)a)->nextRunTime, nb = ((const ScheduledTaskInfo*)b)->nextRunTime;
    int r = (na > nb) ? 1 : (na < nb) ? -1 : 0;
    return g_tsSortAsc[TS_COL_NEXT_RUN] ? r : -r;
}
static int cmp_ts_author(const void *a, const void *b) {
    int r = lstrcmpiW(((const ScheduledTaskInfo*)a)->author, ((const ScheduledTaskInfo*)b)->author);
    return g_tsSortAsc[TS_COL_AUTHOR] ? r : -r;
}
static int cmp_ts_path(const void *a, const void *b) {
    int r = lstrcmpiW(((const ScheduledTaskInfo*)a)->path, ((const ScheduledTaskInfo*)b)->path);
    return g_tsSortAsc[TS_COL_PATH] ? r : -r;
}

/* ========================================================================= */
/* SORT FUNCTIONS                                                            */
/* ========================================================================= */

void SortProcesses(void) {
    static const tm_cmp_fn fns[] = { cmp_proc_name, cmp_proc_pid, cmp_proc_mem, cmp_proc_cpu, cmp_proc_path, cmp_proc_network, cmp_proc_threads, cmp_proc_handles, cmp_proc_gpu, cmp_proc_diskread, cmp_proc_diskwrite };
    EnterCriticalSection(&g_dataLock);
    if (g_procSortColumn >= 0 && g_procSortColumn < PROC_COL_COUNT && g_processCount > 1)
        tm_sort(g_processes, g_processCount, sizeof(ProcessInfo), fns[g_procSortColumn]);
    LeaveCriticalSection(&g_dataLock);
}

void SortAutoruns(void) {
    static const tm_cmp_fn fns[] = { cmp_ar_enabled, cmp_ar_name, cmp_ar_desc, cmp_ar_company, cmp_ar_path, cmp_ar_source, cmp_ar_verified };
    EnterCriticalSection(&g_dataLock);
    if (g_arSortColumn >= 0 && g_arSortColumn < 7 && g_autorunCount > 1)
        tm_sort(g_autoruns, g_autorunCount, sizeof(AutorunInfo), fns[g_arSortColumn]);
    LeaveCriticalSection(&g_dataLock);
}

void SortTaskScheduler(void) {
    static const tm_cmp_fn fns[] = { cmp_ts_name, cmp_ts_status, cmp_ts_trigger, cmp_ts_lastrun, cmp_ts_nextrun, cmp_ts_author, cmp_ts_path };
    EnterCriticalSection(&g_dataLock);
    if (g_tsSortColumn >= 0 && g_tsSortColumn < 7 && g_taskCount > 1)
        tm_sort(g_tasks, g_taskCount, sizeof(ScheduledTaskInfo), fns[g_tsSortColumn]);
    LeaveCriticalSection(&g_dataLock);
}

/* ========================================================================= */
/* COLUMN CLICK HANDLERS                                                     */
/* ========================================================================= */

void OnProcessColumnClick(int column) {
    if (column >= 0 && column < PROC_COL_COUNT) {
        if (g_procSortColumn == column) g_procSortAsc[column] = !g_procSortAsc[column];
        else { g_procSortColumn = column; g_procSortAsc[column] = TRUE; }
        SortProcesses();
        UpdateProcessList();
        UpdateProcessColumnHeaders();
    }
}

void OnAutorunColumnClick(int column) {
    if (column >= 0 && column < 7) {
        if (g_arSortColumn == column) g_arSortAsc[column] = !g_arSortAsc[column];
        else { g_arSortColumn = column; g_arSortAsc[column] = TRUE; }
        SortAutoruns();
        UpdateAutorunList();
        UpdateAutorunColumnHeaders();
    }
}

void OnTaskSchedulerColumnClick(int column) {
    if (column >= 0 && column < 7) {
        if (g_tsSortColumn == column) g_tsSortAsc[column] = !g_tsSortAsc[column];
        else { g_tsSortColumn = column; g_tsSortAsc[column] = TRUE; }
        SortTaskScheduler();
        UpdateTaskSchedulerList();
        UpdateTaskSchedulerColumnHeaders();
    }
}

/* ========================================================================= */
/* HEADER SORT INDICATORS                                                    */
/* ========================================================================= */

static void UpdateColumnHeaders(HWND hListView, int numCols, int sortCol, BOOL *sortAsc) {
    HWND hHeader = ListView_GetHeader(hListView);
    int i;
    HDITEM hdi;
    if (!hHeader) return;
    for (i = 0; i < numCols; i++) {
        memset(&hdi, 0, sizeof(hdi));
        hdi.mask = HDI_FORMAT;
        Header_GetItem(hHeader, i, &hdi);
        hdi.fmt &= ~(HDF_SORTUP | HDF_SORTDOWN);
        Header_SetItem(hHeader, i, &hdi);
    }
    if (sortCol >= 0 && sortCol < numCols) {
        memset(&hdi, 0, sizeof(hdi));
        hdi.mask = HDI_FORMAT;
        Header_GetItem(hHeader, sortCol, &hdi);
        hdi.fmt |= sortAsc[sortCol] ? HDF_SORTUP : HDF_SORTDOWN;
        Header_SetItem(hHeader, sortCol, &hdi);
    }
}

void UpdateProcessColumnHeaders(void) { UpdateColumnHeaders(g_hListView, PROC_COL_COUNT, g_procSortColumn, g_procSortAsc); }
void UpdateAutorunColumnHeaders(void) { UpdateColumnHeaders(g_hArListView, 7, g_arSortColumn, g_arSortAsc); }
void UpdateTaskSchedulerColumnHeaders(void) { UpdateColumnHeaders(g_hTsListView, 7, g_tsSortColumn, g_tsSortAsc); }

/* ========================================================================= */
/* LIST UPDATE WRAPPERS                                                      */
/* ========================================================================= */

void UpdateProcessList(void) { PopulateProcessListView(); }
void UpdateAutorunList(void) { PopulateAutorunListView(); }
void UpdateTaskSchedulerList(void) { PopulateTaskSchedulerListView(); }
