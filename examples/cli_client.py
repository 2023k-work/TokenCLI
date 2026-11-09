"""Optional Python CLI integration sample; TokenCLI itself does not require Python.

Default invocation checks version only. Never starts an experiment or calls a paid API.
"""
import argparse
import json
import subprocess


def invoke_reserved_once(executable, envelope, persist_reservation):
    """Integration seam ONLY; main never calls this function.

    the caller must validate authorization/count admission and durably reserve FIRST.
    No retry, no shell, no auth values, no execution of provider text.
    """
    packet = json.loads(json.dumps(envelope))
    persist_reservation(json.loads(json.dumps(packet)))
    try:
        result = subprocess.run([str(executable), "responses"], input=json.dumps(packet, ensure_ascii=False),
                                text=True, encoding="utf-8", capture_output=True, timeout=120,
                                shell=False)
        evidence = json.loads(result.stdout)
        if not isinstance(evidence, dict) or evidence.get("schema_version") != 1:
            raise ValueError()
        return {"exit_code": result.returncode, "evidence": evidence}
    except (Exception, KeyboardInterrupt):
        raise RuntimeError("CLI_RESULT_UNKNOWN_KEEP_RESERVATION_AND_STOP") from None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("executable", help="Full path to TokenCLI.exe")
    args = parser.parse_args()
    result = subprocess.run([args.executable, "version"], capture_output=True, text=True, encoding="utf-8", timeout=20, shell=False)
    print(json.dumps(json.loads(result.stdout), ensure_ascii=False))
    return result.returncode


if __name__ == "__main__":
    raise SystemExit(main())
