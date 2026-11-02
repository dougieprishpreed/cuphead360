# The console CPU's loads and stores as they are, for probe64.c (assembled as
# written). xsdk's calling convention: a pointer in r3, a 64-bit value in a
# single register (r4 for a store's value, r3 for a load's result).
	.machine cell

	.macro leaf name
	.section .text.\name,"ax",@progbits
	.globl \name
	.type \name,@function
\name:
	.endm

	leaf ch360_raw_ld
	ld 3,0(3)
	blr
	.size ch360_raw_ld,.-ch360_raw_ld

	leaf ch360_raw_std
	std 4,0(3)
	blr
	.size ch360_raw_std,.-ch360_raw_std

	leaf ch360_raw_ldbrx
	ldbrx 3,0,3
	blr
	.size ch360_raw_ldbrx,.-ch360_raw_ldbrx

	leaf ch360_raw_stdbrx
	stdbrx 4,0,3
	blr
	.size ch360_raw_stdbrx,.-ch360_raw_stdbrx

	leaf ch360_raw_lwz
	lwz 3,0(3)
	blr
	.size ch360_raw_lwz,.-ch360_raw_lwz

	leaf ch360_raw_stw
	stw 4,0(3)
	blr
	.size ch360_raw_stw,.-ch360_raw_stw

	leaf ch360_raw_lwbrx
	lwbrx 3,0,3
	blr
	.size ch360_raw_lwbrx,.-ch360_raw_lwbrx

	leaf ch360_raw_stwbrx
	stwbrx 4,0,3
	blr
	.size ch360_raw_stwbrx,.-ch360_raw_stwbrx

# ch360_raw_ld_loop(p, n): n doubleword loads from p, for timing.
	leaf ch360_raw_ld_loop
	mtctr 4
1:	ld 5,0(3)
	bdnz 1b
	blr
	.size ch360_raw_ld_loop,.-ch360_raw_ld_loop

# ch360_raw_lwz_loop(p, n): n word loads from p.
	leaf ch360_raw_lwz_loop
	mtctr 4
1:	lwz 5,0(3)
	bdnz 1b
	blr
	.size ch360_raw_lwz_loop,.-ch360_raw_lwz_loop
