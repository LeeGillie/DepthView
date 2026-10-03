"""Keep DepthView.slnx in step with the repository.

The solution lists every file outside its projects by hand: SDK-style projects pick up their
own files automatically, but solution folders do not, so a file added to docs/ or tests/ is
invisible in Visual Studio's Solution Explorer until someone remembers to list it. This was
found when docs/TUNING-GUIDE.md did not appear.

    python tests/check_slnx.py              check: exit 1 if a committed file is not listed,
                                            or a listed file does not exist (CI runs this)
    python tests/check_slnx.py --fix [extra paths...]
                                            rewrite the solution's folders from the committed
                                            files, plus any extra paths given (for files not
                                            committed yet). Projects are kept where they are.

Files inside a project's folder are the project's business and are never listed. The solution
file itself is not listed either.
"""

import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SLNX = os.path.join(ROOT, "DepthView.slnx")
ROOT_FOLDER = "/Solution Items/"


def committed():
    out = subprocess.run(["git", "ls-files"], cwd=ROOT, capture_output=True, text=True, check=True).stdout
    return {f for f in out.splitlines() if f}


def parse(text):
    files = re.findall(r'<File Path="([^"]+)"', text)
    project_paths = re.findall(r'<Project Path="([^"]+)"', text)
    return files, project_paths


def owned_by_project(path, project_paths):
    return any(path.startswith(os.path.dirname(p) + "/") for p in project_paths)


def folder_of(path):
    d = os.path.dirname(path)
    return ROOT_FOLDER if not d else "/" + d + "/"


def write(files, project_paths):
    folders = {}
    for f in files:
        folders.setdefault(folder_of(f), []).append(f)
    for p in project_paths:
        # A project sits in the solution folder of its parent's parent: src/DepthView/x.csproj
        # in /src/, tests/updater/x.csproj in /tests/.
        parent = os.path.dirname(os.path.dirname(p))
        folders.setdefault("/" + parent + "/" if parent else ROOT_FOLDER, [])

    def order(name):
        return (0 if name == ROOT_FOLDER else 1, name.lower())

    lines = ["<Solution>"]
    for name in sorted(folders, key=order):
        lines.append(f'  <Folder Name="{name}">')
        for f in sorted(folders[name], key=str.lower):
            lines.append(f'    <File Path="{f}" />')
        for p in sorted(project_paths):
            parent = os.path.dirname(os.path.dirname(p))
            if ("/" + parent + "/" if parent else ROOT_FOLDER) == name:
                lines.append(f'    <Project Path="{p}" />')
        lines.append("  </Folder>")
    lines.append("</Solution>")
    with open(SLNX, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(lines) + "\n")


def main(argv):
    with open(SLNX, encoding="utf-8") as fh:
        listed, project_paths = parse(fh.read())

    tracked = {f for f in committed()
               if f != "DepthView.slnx" and not owned_by_project(f, project_paths)}

    if argv and argv[0] == "--fix":
        extra = {p.replace("\\", "/") for p in argv[1:]}
        missing_extra = [p for p in extra if not os.path.exists(os.path.join(ROOT, p))]
        if missing_extra:
            print("not found: " + ", ".join(sorted(missing_extra)))
            return 2
        write(sorted(tracked | extra), project_paths)
        print(f"DepthView.slnx rewritten: {len(tracked | extra)} files, {len(project_paths)} projects.")
        return 0

    problems = []
    for f in sorted(tracked - set(listed)):
        problems.append(f"committed but not in the solution: {f}")
    for f in listed:
        if not os.path.exists(os.path.join(ROOT, f)):
            problems.append(f"in the solution but not on disk: {f}")
    if problems:
        print("\n".join(problems))
        print(f"\n{len(problems)} problem(s). Run: python tests/check_slnx.py --fix")
        return 1
    print(f"Solution in step with the repository: {len(listed)} files listed.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
