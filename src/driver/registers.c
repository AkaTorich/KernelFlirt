/*
 * KernelFlirt - Register operations
 * registers.c - Read/Write thread context (registers)
 *
 * Direct KTRAP_FRAME read — same approach as kernel debuggers (KD/WinDbg).
 * No APC required. Works on suspended threads.
 *
 * KTHREAD.TrapFrame offset: 0x90 on all x64 Windows 10/11 builds.
 * KTRAP_FRAME layout: stable on x64 since Windows 10 RTM.
 */

#include <ntddk.h>
#include "ntundoc.h"
#include "../../include/kf_shared.h"

/* ================================================================== */
/*  KTRAP_FRAME offsets on x64 (from public debug symbols)             */
/* ================================================================== */

#define KTHREAD_TRAPFRAME_OFFSET  0x90

/* Volatile registers (saved by syscall/interrupt handler) */
#define TF_RAX      0x30
#define TF_RCX      0x38
#define TF_RDX      0x40
#define TF_R8       0x48
#define TF_R9       0x50
#define TF_R10      0x58
#define TF_R11      0x60

/* 0xD0 = FaultAddress/ContextRecord (8 bytes) */

/* Debug registers */
#define TF_DR0      0xD8
#define TF_DR1      0xE0
#define TF_DR2      0xE8
#define TF_DR3      0xF0
#define TF_DR6      0xF8
#define TF_DR7      0x100

/* 0x108-0x12F: DebugControl, LastBranch*, LastException* */

/* Segment registers */
#define TF_SEGDS    0x130
#define TF_SEGES    0x132
#define TF_SEGFS    0x134
#define TF_SEGGS    0x136

/* 0x138 = TrapFrame pointer (previous trap frame) */

/* Non-volatile registers (saved explicitly by kernel) */
#define TF_RBX      0x140
#define TF_RDI      0x148
#define TF_RSI      0x150
#define TF_RBP      0x158

/* 0x160 = ErrorCode / ExceptionFrame */

/* Hardware frame (pushed by CPU on interrupt/syscall) */
#define TF_RIP      0x168
#define TF_SEGCS    0x170
#define TF_EFLAGS   0x178
#define TF_RSP      0x180
#define TF_SEGSS    0x188

/* Helper macros for reading from trap frame base pointer */
#define TF_READ64(base, off)  (*(ULONG64 *)((UCHAR *)(base) + (off)))
#define TF_READ32(base, off)  (*(ULONG *)((UCHAR *)(base) + (off)))
#define TF_READ16(base, off)  (*(USHORT *)((UCHAR *)(base) + (off)))

/* Kernel addresses on x64 are above this threshold */
#define IS_KERN_PTR(p)  ((ULONG_PTR)(p) > 0xFFFF800000000000ULL)

NTSTATUS
KfReadRegisters(
    _In_ PIRP               Irp,
    _In_ PIO_STACK_LOCATION  IoStack
)
{
    PKF_THREAD_TARGET   input;
    PKF_REGISTERS       output;
    PETHREAD            thread = NULL;
    NTSTATUS            status;
    PVOID               pTrapFrame;

    if (IoStack->Parameters.DeviceIoControl.InputBufferLength < sizeof(KF_THREAD_TARGET)) {
        Irp->IoStatus.Information = 0;
        return STATUS_BUFFER_TOO_SMALL;
    }

    if (IoStack->Parameters.DeviceIoControl.OutputBufferLength < sizeof(KF_REGISTERS)) {
        Irp->IoStatus.Information = 0;
        return STATUS_BUFFER_TOO_SMALL;
    }

    input  = (PKF_THREAD_TARGET)Irp->AssociatedIrp.SystemBuffer;

    /* Save input before output overwrites SystemBuffer */
    ULONG targetTid = input->ThreadId;

    output = (PKF_REGISTERS)Irp->AssociatedIrp.SystemBuffer;

    status = PsLookupThreadByThreadId((HANDLE)(ULONG_PTR)targetTid, &thread);
    if (!NT_SUCCESS(status)) {
        DbgPrint("[KernelFlirt] ReadRegs: PsLookupThread(%u) failed 0x%08X\n", targetTid, status);
        Irp->IoStatus.Information = 0;
        return status;
    }

    /*
     * Read TrapFrame pointer from KTHREAD at offset 0x90.
     * This is stable across all x64 Windows 10/11 builds (10240 – 26100+).
     */
    pTrapFrame = *(PVOID *)((UCHAR *)thread + KTHREAD_TRAPFRAME_OFFSET);

    if (!IS_KERN_PTR(pTrapFrame)) {
        DbgPrint("[KernelFlirt] ReadRegs(TID %u): TrapFrame=%p — invalid, thread may be running in user mode\n",
                 targetTid, pTrapFrame);
        ObDereferenceObject(thread);
        Irp->IoStatus.Information = 0;
        return STATUS_UNSUCCESSFUL;
    }

    /*
     * Validate that the trap frame pages are resident before reading.
     * Service processes (spoolsv, svchost, etc.) can have threads in
     * deep kernel wait whose trap frames are paged out. Reading paged-out
     * nonpaged-expectation memory causes PAGE_FAULT_IN_NONPAGED_AREA (BSOD)
     * which __try/__except cannot catch.
     */
    if (!MmIsAddressValid(pTrapFrame) ||
        !MmIsAddressValid((UCHAR *)pTrapFrame + TF_SEGSS)) {
        DbgPrint("[KernelFlirt] ReadRegs(TID %u): TrapFrame=%p — pages not resident (paged out)\n",
                 targetTid, pTrapFrame);
        ObDereferenceObject(thread);
        Irp->IoStatus.Information = 0;
        return STATUS_UNSUCCESSFUL;
    }

    DbgPrint("[KernelFlirt] ReadRegs(TID %u): TrapFrame=%p\n", targetTid, pTrapFrame);

    __try {
        /* Volatile registers */
        output->Rax = TF_READ64(pTrapFrame, TF_RAX);
        output->Rcx = TF_READ64(pTrapFrame, TF_RCX);
        output->Rdx = TF_READ64(pTrapFrame, TF_RDX);
        output->R8  = TF_READ64(pTrapFrame, TF_R8);
        output->R9  = TF_READ64(pTrapFrame, TF_R9);
        output->R10 = TF_READ64(pTrapFrame, TF_R10);
        output->R11 = TF_READ64(pTrapFrame, TF_R11);

        /* Non-volatile (saved by kernel in trap frame) */
        output->Rbx = TF_READ64(pTrapFrame, TF_RBX);
        output->Rdi = TF_READ64(pTrapFrame, TF_RDI);
        output->Rsi = TF_READ64(pTrapFrame, TF_RSI);
        output->Rbp = TF_READ64(pTrapFrame, TF_RBP);

        /* R12-R15 are in KEXCEPTION_FRAME, not KTRAP_FRAME.
           Set to 0 for now — they're non-volatile and rarely needed for debugging. */
        output->R12 = 0;
        output->R13 = 0;
        output->R14 = 0;
        output->R15 = 0;

        /* Hardware frame (pushed by CPU) */
        output->Rip    = TF_READ64(pTrapFrame, TF_RIP);
        output->Rsp    = TF_READ64(pTrapFrame, TF_RSP);
        output->Rflags = TF_READ64(pTrapFrame, TF_EFLAGS);

        /* Segment registers */
        output->Cs = TF_READ16(pTrapFrame, TF_SEGCS);
        output->Ss = TF_READ16(pTrapFrame, TF_SEGSS);
        output->Ds = TF_READ16(pTrapFrame, TF_SEGDS);
        output->Es = TF_READ16(pTrapFrame, TF_SEGES);
        output->Fs = TF_READ16(pTrapFrame, TF_SEGFS);
        output->Gs = TF_READ16(pTrapFrame, TF_SEGGS);

        /* Debug registers */
        output->Dr0 = TF_READ64(pTrapFrame, TF_DR0);
        output->Dr1 = TF_READ64(pTrapFrame, TF_DR1);
        output->Dr2 = TF_READ64(pTrapFrame, TF_DR2);
        output->Dr3 = TF_READ64(pTrapFrame, TF_DR3);
        output->Dr6 = TF_READ64(pTrapFrame, TF_DR6);
        output->Dr7 = TF_READ64(pTrapFrame, TF_DR7);

        status = STATUS_SUCCESS;
        Irp->IoStatus.Information = sizeof(KF_REGISTERS);

        DbgPrint("[KernelFlirt] ReadRegs(TID %u): OK RIP=%p RSP=%p\n",
                 targetTid, (PVOID)output->Rip, (PVOID)output->Rsp);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        status = GetExceptionCode();
        DbgPrint("[KernelFlirt] ReadRegs(TID %u): exception 0x%08X reading TrapFrame\n",
                 targetTid, status);
        Irp->IoStatus.Information = 0;
    }

    ObDereferenceObject(thread);
    return status;
}

NTSTATUS
KfWriteRegisters(
    _In_ PIRP               Irp,
    _In_ PIO_STACK_LOCATION  IoStack
)
{
    PKF_WRITE_REGISTERS_IN  input;
    PETHREAD                thread = NULL;
    NTSTATUS                status;
    PVOID                   pTrapFrame;

    if (IoStack->Parameters.DeviceIoControl.InputBufferLength < sizeof(KF_WRITE_REGISTERS_IN)) {
        Irp->IoStatus.Information = 0;
        return STATUS_BUFFER_TOO_SMALL;
    }

    input = (PKF_WRITE_REGISTERS_IN)Irp->AssociatedIrp.SystemBuffer;

    status = PsLookupThreadByThreadId((HANDLE)(ULONG_PTR)input->Target.ThreadId, &thread);
    if (!NT_SUCCESS(status)) {
        Irp->IoStatus.Information = 0;
        return status;
    }

    pTrapFrame = *(PVOID *)((UCHAR *)thread + KTHREAD_TRAPFRAME_OFFSET);

    if (!IS_KERN_PTR(pTrapFrame)) {
        ObDereferenceObject(thread);
        Irp->IoStatus.Information = 0;
        return STATUS_UNSUCCESSFUL;
    }

    if (!MmIsAddressValid(pTrapFrame) ||
        !MmIsAddressValid((UCHAR *)pTrapFrame + TF_SEGSS)) {
        DbgPrint("[KernelFlirt] WriteRegs(TID %u): TrapFrame=%p — pages not resident\n",
                 input->Target.ThreadId, pTrapFrame);
        ObDereferenceObject(thread);
        Irp->IoStatus.Information = 0;
        return STATUS_UNSUCCESSFUL;
    }

    __try {
        /* Write volatile registers */
        *(ULONG64 *)((UCHAR *)pTrapFrame + TF_RAX) = input->Registers.Rax;
        *(ULONG64 *)((UCHAR *)pTrapFrame + TF_RCX) = input->Registers.Rcx;
        *(ULONG64 *)((UCHAR *)pTrapFrame + TF_RDX) = input->Registers.Rdx;
        *(ULONG64 *)((UCHAR *)pTrapFrame + TF_R8)  = input->Registers.R8;
        *(ULONG64 *)((UCHAR *)pTrapFrame + TF_R9)  = input->Registers.R9;
        *(ULONG64 *)((UCHAR *)pTrapFrame + TF_R10) = input->Registers.R10;
        *(ULONG64 *)((UCHAR *)pTrapFrame + TF_R11) = input->Registers.R11;

        /* Non-volatile */
        *(ULONG64 *)((UCHAR *)pTrapFrame + TF_RBX) = input->Registers.Rbx;
        *(ULONG64 *)((UCHAR *)pTrapFrame + TF_RDI) = input->Registers.Rdi;
        *(ULONG64 *)((UCHAR *)pTrapFrame + TF_RSI) = input->Registers.Rsi;
        *(ULONG64 *)((UCHAR *)pTrapFrame + TF_RBP) = input->Registers.Rbp;

        /* Hardware frame */
        *(ULONG64 *)((UCHAR *)pTrapFrame + TF_RIP) = input->Registers.Rip;
        *(ULONG64 *)((UCHAR *)pTrapFrame + TF_RSP) = input->Registers.Rsp;
        *(ULONG64 *)((UCHAR *)pTrapFrame + TF_EFLAGS) = input->Registers.Rflags;

        /* Segments */
        *(USHORT *)((UCHAR *)pTrapFrame + TF_SEGCS) = input->Registers.Cs;
        *(USHORT *)((UCHAR *)pTrapFrame + TF_SEGSS) = input->Registers.Ss;
        *(USHORT *)((UCHAR *)pTrapFrame + TF_SEGDS) = input->Registers.Ds;
        *(USHORT *)((UCHAR *)pTrapFrame + TF_SEGES) = input->Registers.Es;
        *(USHORT *)((UCHAR *)pTrapFrame + TF_SEGFS) = input->Registers.Fs;
        *(USHORT *)((UCHAR *)pTrapFrame + TF_SEGGS) = input->Registers.Gs;

        /* Debug registers */
        *(ULONG64 *)((UCHAR *)pTrapFrame + TF_DR0) = input->Registers.Dr0;
        *(ULONG64 *)((UCHAR *)pTrapFrame + TF_DR1) = input->Registers.Dr1;
        *(ULONG64 *)((UCHAR *)pTrapFrame + TF_DR2) = input->Registers.Dr2;
        *(ULONG64 *)((UCHAR *)pTrapFrame + TF_DR3) = input->Registers.Dr3;
        *(ULONG64 *)((UCHAR *)pTrapFrame + TF_DR6) = input->Registers.Dr6;
        *(ULONG64 *)((UCHAR *)pTrapFrame + TF_DR7) = input->Registers.Dr7;

        status = STATUS_SUCCESS;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        status = GetExceptionCode();
    }

    ObDereferenceObject(thread);
    Irp->IoStatus.Information = 0;
    return status;
}

/*
 * KfWriteRip — Modifies ONLY the RIP field in the trap frame.
 * Unlike KfWriteRegisters, this does NOT touch any other registers
 * (R12-R15, segments, debug regs), preventing state corruption.
 */
NTSTATUS
KfWriteRip(
    _In_ PIRP               Irp,
    _In_ PIO_STACK_LOCATION  IoStack
)
{
    PKF_WRITE_RIP_IN    input;
    PETHREAD            thread = NULL;
    NTSTATUS            status;
    PVOID               pTrapFrame;

    if (IoStack->Parameters.DeviceIoControl.InputBufferLength < sizeof(KF_WRITE_RIP_IN)) {
        Irp->IoStatus.Information = 0;
        return STATUS_BUFFER_TOO_SMALL;
    }

    input = (PKF_WRITE_RIP_IN)Irp->AssociatedIrp.SystemBuffer;

    status = PsLookupThreadByThreadId((HANDLE)(ULONG_PTR)input->ThreadId, &thread);
    if (!NT_SUCCESS(status)) {
        Irp->IoStatus.Information = 0;
        return status;
    }

    pTrapFrame = *(PVOID *)((UCHAR *)thread + KTHREAD_TRAPFRAME_OFFSET);

    if (!IS_KERN_PTR(pTrapFrame)) {
        ObDereferenceObject(thread);
        Irp->IoStatus.Information = 0;
        return STATUS_UNSUCCESSFUL;
    }

    if (!MmIsAddressValid(pTrapFrame) ||
        !MmIsAddressValid((UCHAR *)pTrapFrame + TF_RSP)) {
        DbgPrint("[KernelFlirt] WriteRip(TID %u): TrapFrame=%p — pages not resident\n",
                 input->ThreadId, pTrapFrame);
        ObDereferenceObject(thread);
        Irp->IoStatus.Information = 0;
        return STATUS_UNSUCCESSFUL;
    }

    __try {
        *(ULONG64 *)((UCHAR *)pTrapFrame + TF_RIP) = input->NewRip;
        if (input->Flags & KF_WRIP_SET_RSP)
            *(ULONG64 *)((UCHAR *)pTrapFrame + TF_RSP) = input->NewRsp;
        status = STATUS_SUCCESS;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        status = GetExceptionCode();
    }

    ObDereferenceObject(thread);
    Irp->IoStatus.Information = 0;
    return status;
}
