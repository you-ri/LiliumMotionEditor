"""Run one Kimodo generation for the Kimodo window of Lilium Motion Editor, without Docker.

The window copies this script, kimodo_constraints.py and kimodo_generate.py into a work folder
next to the exported keys (keys.bvh, export.json, prompt.txt), and runs it with the Python of
the environment where Kimodo is installed:

  python kimodo_run.py <work folder> <fullbody|ends|root>

It does what run.sh does in the Docker container: makes the constraints, generates gen.bvh, writes
everything to log.txt, and writes the exit code to exit.txt when it is done (the window reads it).
"""

import json
import os
import subprocess
import sys


def main():
    work = os.path.abspath(sys.argv[1])
    mode = sys.argv[2]
    here = os.path.dirname(os.path.abspath(__file__))
    code = 1
    with open(os.path.join(work, "log.txt"), "w", encoding="utf-8", buffering=1) as log:
        try:
            constraints = os.path.join(work, "constraints.json")
            made = subprocess.run(
                [sys.executable, os.path.join(here, "kimodo_constraints.py"),
                 os.path.join(work, "keys.bvh"), os.path.join(work, "export.json"), constraints, "--mode", mode],
                stdout=subprocess.PIPE, stderr=log, text=True)
            log.write(made.stdout)
            code = made.returncode
            if code == 0:
                duration = json.loads(made.stdout.strip().splitlines()[-1])["duration"]
                with open(os.path.join(work, "prompt.txt"), encoding="utf-8") as f:
                    prompt = f.read()
                code = subprocess.call(
                    [sys.executable, os.path.join(here, "kimodo_generate.py"), prompt, "--duration", str(duration),
                     "--constraints", constraints, "--output", os.path.join(work, "gen"), "--bvh", "--bvh_standard_tpose"],
                    stdout=log, stderr=subprocess.STDOUT)
        except Exception as error:
            log.write("%s: %s\n" % (type(error).__name__, error))
            code = 1
    # Write the code, then rename, so the window never reads a half-written file
    tmp = os.path.join(work, "exit.tmp")
    with open(tmp, "w") as f:
        f.write(str(code))
    os.replace(tmp, os.path.join(work, "exit.txt"))
    return code


if __name__ == "__main__":
    sys.exit(main())
