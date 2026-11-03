# The console CPU's 64-bit results read back right after they're produced,
# for probe64.c (assembled as written). Each hz_P_C_D function:
#   r10 <- a known stale value (0x7EADBEEF in the high word), settled by nops
#   producer P writes r10 from the inputs (r3, r4; r5 a pointer to load from)
#   D nops
#   consumer C reads r10 (r6 a pointer to store to); the result in r3
# xsdk's calling convention: 64-bit arguments in single registers r3-r6.
	.machine cell

	.macro preset
	lis 10,0x7EAD
	ori 10,10,0xBEEF
	sldi 10,10,32
	.rept 16
	nop
	.endr
	.endm

	.macro produce p
	.ifc \p,or
	or 10,3,4
	.endif
	.ifc \p,so
	sldi 10,3,32
	or 10,10,4
	.endif
	.ifc \p,add
	add 10,3,4
	.endif
	.ifc \p,ld
	ld 10,0(5)
	.endif
	.ifc \p,mul
	mulld 10,3,4
	.endif
	.endm

	.macro consume c
	.ifc \c,cmp
	cmpdi 0,10,0
	mfcr 3
	srwi 3,3,28
	.endif
	.ifc \c,srd
	srdi 3,10,32
	.endif
	.ifc \c,std
	std 10,0(6)
	li 3,0
	.endif
	.ifc \c,mr
	mr 3,10
	.endif
	.ifc \c,add
	addi 3,10,0
	.endif
	.endm

	.macro hz p, c, d
	.section .text.hz_\p\()_\c\()_\d,"ax",@progbits
	.globl hz_\p\()_\c\()_\d
	.type hz_\p\()_\c\()_\d,@function
hz_\p\()_\c\()_\d:
	preset
	produce \p
	.rept \d
	nop
	.endr
	consume \c
	blr
	.size hz_\p\()_\c\()_\d,.-hz_\p\()_\c\()_\d
	.endm

	.irp p, or, so, add, ld, mul
	.irp c, cmp, srd, std, mr, add
	.irp d, 0, 1, 2, 3, 4, 6, 8, 12
	hz \p, \c, \d
	.endr
	.endr
	.endr

# The calling thread's machine state register (titles run in supervisor
# state: xsdk docs/HARDWARE.md read 0x9030 the same way).
	.section .text.ch360_raw_mfmsr,"ax",@progbits
	.globl ch360_raw_mfmsr
	.type ch360_raw_mfmsr,@function
ch360_raw_mfmsr:
	mfmsr 3
	blr
	.size ch360_raw_mfmsr,.-ch360_raw_mfmsr

	.macro entry p, c, d
	.long hz_\p\()_\c\()_\d
	.endm

# The table probe64.c indexes [producer][consumer][distance].
	.section .data.ch360_hz_table,"aw",@progbits
	.balign 4
	.globl ch360_hz_table
ch360_hz_table:
	.irp p, or, so, add, ld, mul
	.irp c, cmp, srd, std, mr, add
	.irp d, 0, 1, 2, 3, 4, 6, 8, 12
	entry \p, \c, \d
	.endr
	.endr
	.endr
