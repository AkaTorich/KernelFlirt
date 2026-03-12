/* taskschd.c - Task Scheduler management via COM in C (no CRT) */
#include <initguid.h>
#include <taskschd.h>
#include "taskman.h"

/* ========================================================================= */
/* MODULE STATE                                                              */
/* ========================================================================= */

static ITaskService *s_pService = NULL;
static ITaskFolder  *s_pRootFolder = NULL;
static BOOL          s_bInitialized = FALSE;

/* ========================================================================= */
/* FORWARD DECLARATIONS                                                      */
/* ========================================================================= */

static BOOL EnumerateTasksInFolder(ITaskFolder *pFolder, const wchar_t *folderPath);
static void ParseTaskInfo(IRegisteredTask *pTask, const wchar_t *taskPath, ScheduledTaskInfo *ti);
static void GetTriggerDescription(ITriggerCollection *pTriggers, wchar_t *buf, int bufSize);
static void GetExecutableFromActions(IActionCollection *pActions, ScheduledTaskInfo *ti);

/* ========================================================================= */
/* VARIANT HELPER                                                            */
/* ========================================================================= */

static VARIANT VarEmpty(void) {
    VARIANT v;
    VariantInit(&v);
    return v;
}

static VARIANT VarBstr(const wchar_t *s) {
    VARIANT v;
    VariantInit(&v);
    v.vt = VT_BSTR;
    v.bstrVal = SysAllocString(s);
    return v;
}

static VARIANT VarLong(LONG val) {
    VARIANT v;
    VariantInit(&v);
    v.vt = VT_I4;
    v.lVal = val;
    return v;
}

/* ========================================================================= */
/* INITIALIZATION AND CLEANUP                                                */
/* ========================================================================= */

BOOL TS_Initialize(void) {
    HRESULT hr;
    VARIANT vEmpty;
    BSTR rootPath;

    if (s_bInitialized) return TRUE;

    hr = CoInitializeEx(NULL, COINIT_MULTITHREADED);
    if (FAILED(hr)) return FALSE;

    hr = CoCreateInstance(&CLSID_TaskScheduler, NULL, CLSCTX_INPROC_SERVER,
                          &IID_ITaskService, (void**)&s_pService);
    if (FAILED(hr)) { CoUninitialize(); return FALSE; }

    vEmpty = VarEmpty();
    hr = s_pService->lpVtbl->Connect(s_pService, vEmpty, vEmpty, vEmpty, vEmpty);
    if (FAILED(hr)) {
        s_pService->lpVtbl->Release(s_pService);
        s_pService = NULL;
        CoUninitialize();
        return FALSE;
    }

    rootPath = SysAllocString(L"\\");
    hr = s_pService->lpVtbl->GetFolder(s_pService, rootPath, &s_pRootFolder);
    SysFreeString(rootPath);
    if (FAILED(hr)) {
        s_pService->lpVtbl->Release(s_pService);
        s_pService = NULL;
        CoUninitialize();
        return FALSE;
    }

    s_bInitialized = TRUE;
    return TRUE;
}

void TS_Cleanup(void) {
    if (s_pRootFolder) {
        s_pRootFolder->lpVtbl->Release(s_pRootFolder);
        s_pRootFolder = NULL;
    }
    if (s_pService) {
        s_pService->lpVtbl->Release(s_pService);
        s_pService = NULL;
    }
    if (s_bInitialized) {
        CoUninitialize();
        s_bInitialized = FALSE;
    }
}

/* ========================================================================= */
/* TASK ENUMERATION                                                          */
/* ========================================================================= */

void TS_EnumerateAllTasks(void) {
    DYNARRAY_FREE(g_tasks, g_taskCount, g_taskCap);
    if (!s_bInitialized || !s_pRootFolder) return;
    EnumerateTasksInFolder(s_pRootFolder, L"\\");
}

static BOOL EnumerateTasksInFolder(ITaskFolder *pFolder, const wchar_t *folderPath) {
    IRegisteredTaskCollection *pTaskCollection = NULL;
    ITaskFolderCollection *pFolderCollection = NULL;
    HRESULT hr;
    LONG count, i;

    if (!pFolder) return FALSE;

    /* Enumerate tasks in current folder */
    hr = pFolder->lpVtbl->GetTasks(pFolder, TASK_ENUM_HIDDEN, &pTaskCollection);
    if (SUCCEEDED(hr)) {
        count = 0;
        pTaskCollection->lpVtbl->get_Count(pTaskCollection, &count);
        for (i = 0; i < count; i++) {
            IRegisteredTask *pTask = NULL;
            VARIANT vIdx = VarLong(i + 1);
            hr = pTaskCollection->lpVtbl->get_Item(pTaskCollection, vIdx, &pTask);
            if (SUCCEEDED(hr)) {
                ScheduledTaskInfo ti;
                memset(&ti, 0, sizeof(ti));
                ParseTaskInfo(pTask, folderPath, &ti);
                DYNARRAY_GROW(g_tasks, g_taskCount, g_taskCap, ScheduledTaskInfo);
                g_tasks[g_taskCount++] = ti;
                pTask->lpVtbl->Release(pTask);
            }
        }
        pTaskCollection->lpVtbl->Release(pTaskCollection);
    }

    /* Enumerate subfolders */
    hr = pFolder->lpVtbl->GetFolders(pFolder, 0, &pFolderCollection);
    if (SUCCEEDED(hr)) {
        count = 0;
        pFolderCollection->lpVtbl->get_Count(pFolderCollection, &count);
        for (i = 0; i < count; i++) {
            ITaskFolder *pSubFolder = NULL;
            VARIANT vIdx = VarLong(i + 1);
            hr = pFolderCollection->lpVtbl->get_Item(pFolderCollection, vIdx, &pSubFolder);
            if (SUCCEEDED(hr)) {
                BSTR folderName = NULL;
                hr = pSubFolder->lpVtbl->get_Name(pSubFolder, &folderName);
                if (SUCCEEDED(hr) && folderName) {
                    wchar_t subPath[TM_MAX_PATH_BUF];
                    lstrcpynW(subPath, folderPath, TM_MAX_PATH_BUF);
                    if (lstrcmpW(subPath, L"\\") != 0)
                        tm_wcscat_s(subPath, TM_MAX_PATH_BUF, L"\\");
                    tm_wcscat_s(subPath, TM_MAX_PATH_BUF, folderName);
                    EnumerateTasksInFolder(pSubFolder, subPath);
                    SysFreeString(folderName);
                }
                pSubFolder->lpVtbl->Release(pSubFolder);
            }
        }
        pFolderCollection->lpVtbl->Release(pFolderCollection);
    }

    return TRUE;
}

static void ParseTaskInfo(IRegisteredTask *pTask, const wchar_t *taskPath, ScheduledTaskInfo *ti) {
    BSTR bstr = NULL;
    VARIANT_BOOL vb;
    DATE dt;
    ITaskDefinition *pDef = NULL;
    HRESULT hr;

    /* Name */
    if (SUCCEEDED(pTask->lpVtbl->get_Name(pTask, &bstr)) && bstr) {
        lstrcpynW(ti->name, bstr, TM_MAX_NAME);
        SysFreeString(bstr); bstr = NULL;
    }

    /* Path */
    if (lstrcmpW(taskPath, L"\\") == 0) {
        wsprintfW(ti->path, L"\\%s", ti->name);
    } else {
        lstrcpynW(ti->path, taskPath, TM_MAX_PATH_BUF);
        tm_wcscat_s(ti->path, TM_MAX_PATH_BUF, L"\\");
        tm_wcscat_s(ti->path, TM_MAX_PATH_BUF, ti->name);
    }

    /* State */
    pTask->lpVtbl->get_State(pTask, (TASK_STATE*)&ti->state);

    /* Enabled */
    vb = VARIANT_FALSE;
    if (SUCCEEDED(pTask->lpVtbl->get_Enabled(pTask, &vb)))
        ti->enabled = (vb == VARIANT_TRUE);

    /* Last/Next run time */
    dt = 0;
    if (SUCCEEDED(pTask->lpVtbl->get_LastRunTime(pTask, &dt)))
        ti->lastRunTime = dt;
    dt = 0;
    if (SUCCEEDED(pTask->lpVtbl->get_NextRunTime(pTask, &dt)))
        ti->nextRunTime = dt;

    /* Task definition */
    hr = pTask->lpVtbl->get_Definition(pTask, &pDef);
    if (SUCCEEDED(hr) && pDef) {
        IRegistrationInfo *pReg = NULL;
        ITaskSettings *pSettings = NULL;
        ITriggerCollection *pTriggers = NULL;
        IActionCollection *pActions = NULL;

        /* Registration info */
        if (SUCCEEDED(pDef->lpVtbl->get_RegistrationInfo(pDef, &pReg)) && pReg) {
            bstr = NULL;
            if (SUCCEEDED(pReg->lpVtbl->get_Description(pReg, &bstr)) && bstr) {
                lstrcpynW(ti->description, bstr, TM_MAX_DESC);
                SysFreeString(bstr); bstr = NULL;
            }
            if (SUCCEEDED(pReg->lpVtbl->get_Author(pReg, &bstr)) && bstr) {
                lstrcpynW(ti->author, bstr, TM_MAX_NAME);
                SysFreeString(bstr); bstr = NULL;
            }
            pReg->lpVtbl->Release(pReg);
        }

        /* Settings */
        if (SUCCEEDED(pDef->lpVtbl->get_Settings(pDef, &pSettings)) && pSettings) {
            vb = VARIANT_FALSE;
            if (SUCCEEDED(pSettings->lpVtbl->get_Hidden(pSettings, &vb)))
                ti->hidden = (vb == VARIANT_TRUE);
            pSettings->lpVtbl->Release(pSettings);
        }

        /* Triggers */
        if (SUCCEEDED(pDef->lpVtbl->get_Triggers(pDef, &pTriggers)) && pTriggers) {
            LONG tc = 0;
            if (SUCCEEDED(pTriggers->lpVtbl->get_Count(pTriggers, &tc)))
                ti->triggerCount = tc;
            GetTriggerDescription(pTriggers, ti->triggerDescription, TM_MAX_TRIGGER);
            pTriggers->lpVtbl->Release(pTriggers);
        }

        /* Actions */
        if (SUCCEEDED(pDef->lpVtbl->get_Actions(pDef, &pActions)) && pActions) {
            GetExecutableFromActions(pActions, ti);
            pActions->lpVtbl->Release(pActions);
        }

        pDef->lpVtbl->Release(pDef);
    }
}

static void GetTriggerDescription(ITriggerCollection *pTriggers, wchar_t *buf, int bufSize) {
    LONG count = 0, i;
    const wchar_t *typeStr;

    buf[0] = 0;
    if (!pTriggers) return;
    if (FAILED(pTriggers->lpVtbl->get_Count(pTriggers, &count)) || count == 0) {
        lstrcpynW(buf, L"No triggers", bufSize);
        return;
    }

    for (i = 0; i < count && i < 3; i++) {
        ITrigger *pTrigger = NULL;
        if (SUCCEEDED(pTriggers->lpVtbl->get_Item(pTriggers, i + 1, &pTrigger))) {
            TASK_TRIGGER_TYPE2 tt;
            if (SUCCEEDED(pTrigger->lpVtbl->get_Type(pTrigger, &tt))) {
                if (i > 0) tm_wcscat_s(buf, bufSize, L", ");
                switch (tt) {
                    case TASK_TRIGGER_BOOT:    typeStr = L"At startup"; break;
                    case TASK_TRIGGER_LOGON:   typeStr = L"At logon"; break;
                    case TASK_TRIGGER_DAILY:   typeStr = L"Daily"; break;
                    case TASK_TRIGGER_WEEKLY:  typeStr = L"Weekly"; break;
                    case TASK_TRIGGER_MONTHLY: typeStr = L"Monthly"; break;
                    case TASK_TRIGGER_TIME:    typeStr = L"One time"; break;
                    case TASK_TRIGGER_EVENT:   typeStr = L"On event"; break;
                    case TASK_TRIGGER_IDLE:    typeStr = L"On idle"; break;
                    default:                   typeStr = L"Unknown"; break;
                }
                tm_wcscat_s(buf, bufSize, typeStr);
            }
            pTrigger->lpVtbl->Release(pTrigger);
        }
    }
    if (count > 3) tm_wcscat_s(buf, bufSize, L" (+)");
}

static void GetExecutableFromActions(IActionCollection *pActions, ScheduledTaskInfo *ti) {
    LONG count = 0;
    IAction *pAction = NULL;
    TASK_ACTION_TYPE at;
    IExecAction *pExec = NULL;
    BSTR bstr = NULL;
    HRESULT hr;

    if (FAILED(pActions->lpVtbl->get_Count(pActions, &count)) || count == 0) return;

    hr = pActions->lpVtbl->get_Item(pActions, 1, &pAction);
    if (FAILED(hr) || !pAction) return;

    if (SUCCEEDED(pAction->lpVtbl->get_Type(pAction, &at)) && at == TASK_ACTION_EXEC) {
        hr = pAction->lpVtbl->QueryInterface(pAction, &IID_IExecAction, (void**)&pExec);
        if (SUCCEEDED(hr) && pExec) {
            if (SUCCEEDED(pExec->lpVtbl->get_Path(pExec, &bstr)) && bstr) {
                lstrcpynW(ti->executable, bstr, TM_MAX_PATH_BUF);
                SysFreeString(bstr); bstr = NULL;
            }
            if (SUCCEEDED(pExec->lpVtbl->get_Arguments(pExec, &bstr)) && bstr) {
                lstrcpynW(ti->arguments, bstr, TM_MAX_ARGS);
                SysFreeString(bstr); bstr = NULL;
            }
            if (SUCCEEDED(pExec->lpVtbl->get_WorkingDirectory(pExec, &bstr)) && bstr) {
                lstrcpynW(ti->workingDirectory, bstr, TM_MAX_PATH_BUF);
                SysFreeString(bstr); bstr = NULL;
            }
            pExec->lpVtbl->Release(pExec);
        }
    }
    pAction->lpVtbl->Release(pAction);
}

/* ========================================================================= */
/* TASK OPERATIONS                                                           */
/* ========================================================================= */

BOOL TS_CreateSimpleTask(const wchar_t *taskName, const wchar_t *executable,
                         const wchar_t *arguments, const wchar_t *description,
                         BOOL runAtStartup, BOOL runAsAdmin)
{
    ITaskDefinition *pDef = NULL;
    IRegistrationInfo *pReg = NULL;
    ITaskSettings *pSettings = NULL;
    IPrincipal *pPrincipal = NULL;
    ITriggerCollection *pTriggers = NULL;
    ITrigger *pTrigger = NULL;
    IActionCollection *pActions = NULL;
    IAction *pAction = NULL;
    IExecAction *pExec = NULL;
    IRegisteredTask *pRegTask = NULL;
    HRESULT hr;
    BSTR bstr;
    VARIANT vEmpty, vSddl;
    BOOL result = FALSE;

    if (!s_bInitialized || !s_pService) return FALSE;

    hr = s_pService->lpVtbl->NewTask(s_pService, 0, &pDef);
    if (FAILED(hr)) return FALSE;

    /* Registration info */
    if (SUCCEEDED(pDef->lpVtbl->get_RegistrationInfo(pDef, &pReg)) && pReg) {
        if (description && description[0]) {
            bstr = SysAllocString(description);
            pReg->lpVtbl->put_Description(pReg, bstr);
            SysFreeString(bstr);
        }
        bstr = SysAllocString(L"TaskMan Enhanced");
        pReg->lpVtbl->put_Author(pReg, bstr);
        SysFreeString(bstr);
        pReg->lpVtbl->Release(pReg);
    }

    /* Settings */
    if (SUCCEEDED(pDef->lpVtbl->get_Settings(pDef, &pSettings)) && pSettings) {
        pSettings->lpVtbl->put_Enabled(pSettings, VARIANT_TRUE);
        pSettings->lpVtbl->put_AllowDemandStart(pSettings, VARIANT_TRUE);
        pSettings->lpVtbl->put_AllowHardTerminate(pSettings, VARIANT_TRUE);
        pSettings->lpVtbl->put_StartWhenAvailable(pSettings, VARIANT_TRUE);
        pSettings->lpVtbl->Release(pSettings);
    }

    /* Principal */
    if (SUCCEEDED(pDef->lpVtbl->get_Principal(pDef, &pPrincipal)) && pPrincipal) {
        if (runAsAdmin)
            pPrincipal->lpVtbl->put_RunLevel(pPrincipal, TASK_RUNLEVEL_HIGHEST);
        pPrincipal->lpVtbl->put_LogonType(pPrincipal, TASK_LOGON_INTERACTIVE_TOKEN);
        pPrincipal->lpVtbl->Release(pPrincipal);
    }

    /* Trigger */
    if (SUCCEEDED(pDef->lpVtbl->get_Triggers(pDef, &pTriggers)) && pTriggers) {
        hr = pTriggers->lpVtbl->Create(pTriggers,
            runAtStartup ? TASK_TRIGGER_BOOT : TASK_TRIGGER_LOGON, &pTrigger);
        if (SUCCEEDED(hr) && pTrigger) {
            pTrigger->lpVtbl->put_Enabled(pTrigger, VARIANT_TRUE);
            pTrigger->lpVtbl->Release(pTrigger);
        }
        pTriggers->lpVtbl->Release(pTriggers);
    }

    /* Action */
    if (SUCCEEDED(pDef->lpVtbl->get_Actions(pDef, &pActions)) && pActions) {
        hr = pActions->lpVtbl->Create(pActions, TASK_ACTION_EXEC, &pAction);
        if (SUCCEEDED(hr) && pAction) {
            hr = pAction->lpVtbl->QueryInterface(pAction, &IID_IExecAction, (void**)&pExec);
            if (SUCCEEDED(hr) && pExec) {
                bstr = SysAllocString(executable);
                pExec->lpVtbl->put_Path(pExec, bstr);
                SysFreeString(bstr);
                if (arguments && arguments[0]) {
                    bstr = SysAllocString(arguments);
                    pExec->lpVtbl->put_Arguments(pExec, bstr);
                    SysFreeString(bstr);
                }
                pExec->lpVtbl->Release(pExec);
            }
            pAction->lpVtbl->Release(pAction);
        }
        pActions->lpVtbl->Release(pActions);
    }

    /* Register */
    vEmpty = VarEmpty();
    vSddl = VarBstr(L"");
    bstr = SysAllocString(taskName);
    hr = s_pRootFolder->lpVtbl->RegisterTaskDefinition(s_pRootFolder,
        bstr, pDef, TASK_CREATE_OR_UPDATE,
        vEmpty, vEmpty, TASK_LOGON_INTERACTIVE_TOKEN, vSddl, &pRegTask);
    SysFreeString(bstr);
    VariantClear(&vSddl);

    if (pRegTask) {
        pRegTask->lpVtbl->Release(pRegTask);
        result = TRUE;
    }
    result = SUCCEEDED(hr);

    pDef->lpVtbl->Release(pDef);
    return result;
}

BOOL TS_DeleteTask(const wchar_t *taskPath) {
    BSTR bstr;
    HRESULT hr;
    if (!s_bInitialized || !s_pRootFolder) return FALSE;
    bstr = SysAllocString(taskPath);
    hr = s_pRootFolder->lpVtbl->DeleteTask(s_pRootFolder, bstr, 0);
    SysFreeString(bstr);
    return SUCCEEDED(hr);
}

BOOL TS_EnableTask(const wchar_t *taskPath, BOOL enable) {
    BSTR bstr;
    IRegisteredTask *pTask = NULL;
    HRESULT hr;
    if (!s_bInitialized || !s_pRootFolder) return FALSE;
    bstr = SysAllocString(taskPath);
    hr = s_pRootFolder->lpVtbl->GetTask(s_pRootFolder, bstr, &pTask);
    SysFreeString(bstr);
    if (SUCCEEDED(hr) && pTask) {
        hr = pTask->lpVtbl->put_Enabled(pTask, enable ? VARIANT_TRUE : VARIANT_FALSE);
        pTask->lpVtbl->Release(pTask);
        return SUCCEEDED(hr);
    }
    return FALSE;
}

BOOL TS_RunTask(const wchar_t *taskPath) {
    BSTR bstr;
    IRegisteredTask *pTask = NULL;
    IRunningTask *pRunning = NULL;
    VARIANT vEmpty;
    HRESULT hr;
    if (!s_bInitialized || !s_pRootFolder) return FALSE;
    bstr = SysAllocString(taskPath);
    hr = s_pRootFolder->lpVtbl->GetTask(s_pRootFolder, bstr, &pTask);
    SysFreeString(bstr);
    if (SUCCEEDED(hr) && pTask) {
        vEmpty = VarEmpty();
        hr = pTask->lpVtbl->Run(pTask, vEmpty, &pRunning);
        if (pRunning) pRunning->lpVtbl->Release(pRunning);
        pTask->lpVtbl->Release(pTask);
        return SUCCEEDED(hr);
    }
    return FALSE;
}

BOOL TS_StopTask(const wchar_t *taskPath) {
    BSTR bstr;
    IRegisteredTask *pTask = NULL;
    HRESULT hr;
    if (!s_bInitialized || !s_pRootFolder) return FALSE;
    bstr = SysAllocString(taskPath);
    hr = s_pRootFolder->lpVtbl->GetTask(s_pRootFolder, bstr, &pTask);
    SysFreeString(bstr);
    if (SUCCEEDED(hr) && pTask) {
        hr = pTask->lpVtbl->Stop(pTask, 0);
        pTask->lpVtbl->Release(pTask);
        return SUCCEEDED(hr);
    }
    return FALSE;
}

/* ========================================================================= */
/* HELPERS                                                                   */
/* ========================================================================= */

const wchar_t *TS_GetTaskStateString(int state) {
    switch (state) {
        case TM_TASK_STATE_DISABLED: return L"Disabled";
        case TM_TASK_STATE_QUEUED:   return L"Queued";
        case TM_TASK_STATE_READY:    return L"Ready";
        case TM_TASK_STATE_RUNNING:  return L"Running";
        default:                     return L"Unknown";
    }
}

void TS_FormatDate(double date, wchar_t *buf, int bufSize) {
    SYSTEMTIME st;
    if (date == 0.0) {
        lstrcpynW(buf, L"Never", bufSize);
        return;
    }
    if (VariantTimeToSystemTime(date, &st)) {
        wsprintfW(buf, L"%02d/%02d/%04d %02d:%02d:%02d",
                  st.wMonth, st.wDay, st.wYear,
                  st.wHour, st.wMinute, st.wSecond);
    } else {
        lstrcpynW(buf, L"Invalid date", bufSize);
    }
}

BOOL TS_IsTaskSystem(const wchar_t *taskPath) {
    return (StrStrIW(taskPath, L"\\Microsoft\\") != NULL ||
            StrStrIW(taskPath, L"\\Windows\\") != NULL);
}

/* ========================================================================= */
/* ENHANCED TASK CREATION WITH TRIGGER TYPES                                 */
/* ========================================================================= */

BOOL TS_CreateTaskEx(const wchar_t *taskName, const wchar_t *executable,
                     const wchar_t *arguments, const wchar_t *description,
                     int triggerType, const SYSTEMTIME *schedTime, BOOL runAsAdmin)
{
    ITaskDefinition *pDef = NULL;
    IRegistrationInfo *pReg = NULL;
    ITaskSettings *pSettings = NULL;
    IPrincipal *pPrincipal = NULL;
    ITriggerCollection *pTriggers = NULL;
    ITrigger *pTrigger = NULL;
    IActionCollection *pActions = NULL;
    IAction *pAction = NULL;
    IExecAction *pExec = NULL;
    IRegisteredTask *pRegTask = NULL;
    HRESULT hr;
    BSTR bstr;
    VARIANT vEmpty, vSddl;
    BOOL result = FALSE;
    TASK_TRIGGER_TYPE2 comTrigger;

    if (!s_bInitialized || !s_pService) return FALSE;

    hr = s_pService->lpVtbl->NewTask(s_pService, 0, &pDef);
    if (FAILED(hr)) return FALSE;

    /* Registration info */
    if (SUCCEEDED(pDef->lpVtbl->get_RegistrationInfo(pDef, &pReg)) && pReg) {
        if (description && description[0]) {
            bstr = SysAllocString(description);
            pReg->lpVtbl->put_Description(pReg, bstr);
            SysFreeString(bstr);
        }
        bstr = SysAllocString(L"TaskMan Enhanced");
        pReg->lpVtbl->put_Author(pReg, bstr);
        SysFreeString(bstr);
        pReg->lpVtbl->Release(pReg);
    }

    /* Settings */
    if (SUCCEEDED(pDef->lpVtbl->get_Settings(pDef, &pSettings)) && pSettings) {
        pSettings->lpVtbl->put_Enabled(pSettings, VARIANT_TRUE);
        pSettings->lpVtbl->put_AllowDemandStart(pSettings, VARIANT_TRUE);
        pSettings->lpVtbl->put_AllowHardTerminate(pSettings, VARIANT_TRUE);
        pSettings->lpVtbl->put_StartWhenAvailable(pSettings, VARIANT_TRUE);
        pSettings->lpVtbl->Release(pSettings);
    }

    /* Principal */
    if (SUCCEEDED(pDef->lpVtbl->get_Principal(pDef, &pPrincipal)) && pPrincipal) {
        if (runAsAdmin)
            pPrincipal->lpVtbl->put_RunLevel(pPrincipal, TASK_RUNLEVEL_HIGHEST);
        pPrincipal->lpVtbl->put_LogonType(pPrincipal, TASK_LOGON_INTERACTIVE_TOKEN);
        pPrincipal->lpVtbl->Release(pPrincipal);
    }

    /* Map trigger type */
    switch (triggerType) {
    case 0:  comTrigger = TASK_TRIGGER_TIME; break;
    case 1:  comTrigger = TASK_TRIGGER_DAILY; break;
    case 2:  comTrigger = TASK_TRIGGER_WEEKLY; break;
    case 3:  comTrigger = TASK_TRIGGER_LOGON; break;
    case 4:  comTrigger = TASK_TRIGGER_BOOT; break;
    default: comTrigger = TASK_TRIGGER_LOGON; break;
    }

    /* Trigger */
    if (SUCCEEDED(pDef->lpVtbl->get_Triggers(pDef, &pTriggers)) && pTriggers) {
        hr = pTriggers->lpVtbl->Create(pTriggers, comTrigger, &pTrigger);
        if (SUCCEEDED(hr) && pTrigger) {
            pTrigger->lpVtbl->put_Enabled(pTrigger, VARIANT_TRUE);

            /* Set start boundary for time-based triggers */
            if (triggerType <= 2 && schedTime) {
                wchar_t iso[64];
                wsprintfW(iso, L"%04d-%02d-%02dT%02d:%02d:%02d",
                    schedTime->wYear, schedTime->wMonth, schedTime->wDay,
                    schedTime->wHour, schedTime->wMinute, schedTime->wSecond);
                bstr = SysAllocString(iso);
                pTrigger->lpVtbl->put_StartBoundary(pTrigger, bstr);
                SysFreeString(bstr);
            }
            pTrigger->lpVtbl->Release(pTrigger);
        }
        pTriggers->lpVtbl->Release(pTriggers);
    }

    /* Action */
    if (SUCCEEDED(pDef->lpVtbl->get_Actions(pDef, &pActions)) && pActions) {
        hr = pActions->lpVtbl->Create(pActions, TASK_ACTION_EXEC, &pAction);
        if (SUCCEEDED(hr) && pAction) {
            hr = pAction->lpVtbl->QueryInterface(pAction, &IID_IExecAction, (void**)&pExec);
            if (SUCCEEDED(hr) && pExec) {
                bstr = SysAllocString(executable);
                pExec->lpVtbl->put_Path(pExec, bstr);
                SysFreeString(bstr);
                if (arguments && arguments[0]) {
                    bstr = SysAllocString(arguments);
                    pExec->lpVtbl->put_Arguments(pExec, bstr);
                    SysFreeString(bstr);
                }
                pExec->lpVtbl->Release(pExec);
            }
            pAction->lpVtbl->Release(pAction);
        }
        pActions->lpVtbl->Release(pActions);
    }

    /* Register */
    vEmpty = VarEmpty();
    vSddl = VarBstr(L"");
    bstr = SysAllocString(taskName);
    hr = s_pRootFolder->lpVtbl->RegisterTaskDefinition(s_pRootFolder,
        bstr, pDef, TASK_CREATE_OR_UPDATE,
        vEmpty, vEmpty, TASK_LOGON_INTERACTIVE_TOKEN, vSddl, &pRegTask);
    SysFreeString(bstr);
    VariantClear(&vSddl);

    if (pRegTask) {
        pRegTask->lpVtbl->Release(pRegTask);
        result = TRUE;
    }
    result = SUCCEEDED(hr);

    pDef->lpVtbl->Release(pDef);
    return result;
}

/* ========================================================================= */
/* XML EXPORT/IMPORT                                                         */
/* ========================================================================= */

BOOL TS_ExportTaskXml(const wchar_t *taskPath, const wchar_t *filePath) {
    IRegisteredTask *pTask = NULL;
    BSTR bstrPath, bstrXml = NULL;
    HRESULT hr;
    HANDLE hFile;
    DWORD written;
    BOOL result = FALSE;
    unsigned char bom[2] = {0xFF, 0xFE};

    if (!s_bInitialized || !s_pRootFolder) return FALSE;

    bstrPath = SysAllocString(taskPath);
    hr = s_pRootFolder->lpVtbl->GetTask(s_pRootFolder, bstrPath, &pTask);
    SysFreeString(bstrPath);
    if (FAILED(hr) || !pTask) return FALSE;

    hr = pTask->lpVtbl->get_Xml(pTask, &bstrXml);
    if (SUCCEEDED(hr) && bstrXml) {
        hFile = CreateFileW(filePath, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
        if (hFile != INVALID_HANDLE_VALUE) {
            DWORD xmlLen = SysStringLen(bstrXml) * sizeof(wchar_t);
            WriteFile(hFile, bom, 2, &written, NULL);
            WriteFile(hFile, bstrXml, xmlLen, &written, NULL);
            CloseHandle(hFile);
            result = TRUE;
        }
        SysFreeString(bstrXml);
    }
    pTask->lpVtbl->Release(pTask);
    return result;
}

BOOL TS_ImportTaskXml(const wchar_t *filePath) {
    HANDLE hFile;
    DWORD fileSize, bytesRead;
    unsigned char *buf;
    wchar_t *xmlStr = NULL;
    BSTR bstrXml;
    HRESULT hr;
    VARIANT vEmpty, vSddl;
    IRegisteredTask *pTask = NULL;
    BOOL result = FALSE;

    if (!s_bInitialized || !s_pRootFolder) return FALSE;

    hFile = CreateFileW(filePath, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    if (hFile == INVALID_HANDLE_VALUE) return FALSE;

    fileSize = GetFileSize(hFile, NULL);
    if (fileSize == 0 || fileSize == INVALID_FILE_SIZE) { CloseHandle(hFile); return FALSE; }

    buf = (unsigned char*)HeapAlloc(GetProcessHeap(), 0, fileSize + 4);
    if (!buf) { CloseHandle(hFile); return FALSE; }

    ReadFile(hFile, buf, fileSize, &bytesRead, NULL);
    CloseHandle(hFile);
    buf[bytesRead] = 0; buf[bytesRead+1] = 0;

    /* Detect BOM and get wide string */
    if (bytesRead >= 2 && buf[0] == 0xFF && buf[1] == 0xFE) {
        /* UTF-16 LE */
        xmlStr = (wchar_t*)(buf + 2);
    } else {
        /* Assume UTF-8, convert */
        int wLen = MultiByteToWideChar(CP_UTF8, 0, (char*)buf, (int)bytesRead, NULL, 0);
        if (wLen > 0) {
            xmlStr = (wchar_t*)HeapAlloc(GetProcessHeap(), 0, ((SIZE_T)wLen + 1) * sizeof(wchar_t));
            if (xmlStr) {
                MultiByteToWideChar(CP_UTF8, 0, (char*)buf, (int)bytesRead, xmlStr, wLen);
                xmlStr[wLen] = 0;
            }
        }
    }

    if (xmlStr) {
        bstrXml = SysAllocString(xmlStr);
        vEmpty = VarEmpty();
        vSddl = VarBstr(L"");
        hr = s_pRootFolder->lpVtbl->RegisterTask(s_pRootFolder,
            NULL, bstrXml, TASK_CREATE, vEmpty, vEmpty,
            TASK_LOGON_INTERACTIVE_TOKEN, vSddl, &pTask);
        SysFreeString(bstrXml);
        VariantClear(&vSddl);
        if (SUCCEEDED(hr) && pTask) {
            pTask->lpVtbl->Release(pTask);
            result = TRUE;
        }
        /* Free converted buffer if we allocated it */
        if (xmlStr != (wchar_t*)(buf + 2))
            HeapFree(GetProcessHeap(), 0, xmlStr);
    }

    HeapFree(GetProcessHeap(), 0, buf);
    return result;
}
