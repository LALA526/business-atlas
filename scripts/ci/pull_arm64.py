"""Pull an official ARM64 image with a bounded client deadline."""
import subprocess,sys

def pull(image,timeout_seconds=300,executable='docker'):
 try:return subprocess.run([executable,'pull','--platform','linux/arm64',image],timeout=timeout_seconds,check=False).returncode
 except subprocess.TimeoutExpired:
  print('ARM64 image pull exceeded its deadline; stopped the client for bounded retry',file=sys.stderr);return 124
if __name__=='__main__':raise SystemExit(pull(sys.argv[1]))
