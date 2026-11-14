# Hold a lock file the same way filelock's WindowsFileLock does (msvcrt.locking LK_NBLCK on byte 0), for N seconds.
import msvcrt, os, sys, time
fd = os.open(sys.argv[1], os.O_RDWR | os.O_CREAT | os.O_TRUNC)
msvcrt.locking(fd, msvcrt.LK_NBLCK, 1)
print('HELD', sys.argv[1], flush=True)
time.sleep(float(sys.argv[2]))
