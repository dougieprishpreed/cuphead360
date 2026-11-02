# The console CPU's doubleword compares as they are, for probe64.c. Assembled
# as written (only C and C++ go through safecmp.py). xsdk's calling
# convention: 64-bit arguments in single registers r3, r4; the result in r3.
# Each returns CR0's LT/GT/EQ/SO nibble (8 LT, 4 GT, 2 EQ, 1 SO), or a count.
	.machine cell
	.section .text.ch360_raw_probe,"ax",@progbits

	.globl ch360_raw_cmpd
	.type ch360_raw_cmpd,@function
ch360_raw_cmpd:
	cmpd 0,3,4
	mfcr 3
	srwi 3,3,28
	blr
	.size ch360_raw_cmpd,.-ch360_raw_cmpd

	.globl ch360_raw_cmpld
	.type ch360_raw_cmpld,@function
ch360_raw_cmpld:
	cmpld 0,3,4
	mfcr 3
	srwi 3,3,28
	blr
	.size ch360_raw_cmpld,.-ch360_raw_cmpld

	.globl ch360_raw_cmpdi0
	.type ch360_raw_cmpdi0,@function
ch360_raw_cmpdi0:
	cmpdi 0,3,0
	mfcr 3
	srwi 3,3,28
	blr
	.size ch360_raw_cmpdi0,.-ch360_raw_cmpdi0

	.globl ch360_raw_cntlzd
	.type ch360_raw_cntlzd,@function
ch360_raw_cntlzd:
	cntlzd 3,3
	blr
	.size ch360_raw_cntlzd,.-ch360_raw_cntlzd
