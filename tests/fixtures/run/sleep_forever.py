"""Prints its process id, then sleeps until killed."""
import os
import time

print(os.getpid(), flush=True)
while True:
    time.sleep(1)
