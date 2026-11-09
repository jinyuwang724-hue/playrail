"""Actual native message-loop exit, keeping stdin open after its agent dies."""
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import tempfile
import threading
import unittest

ROOT = Path(__file__).resolve().parents[1]
EXE = ROOT / 'artifacts/library-toggle/router-recovery/OwnerTest.exe'


class LifetimeTests(unittest.TestCase):
    def exercise(self, close_stdin):
        with tempfile.TemporaryDirectory() as folder:
            owner = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(60)'], creationflags=subprocess.CREATE_NO_WINDOW)
            router = None
            try:
                router = subprocess.Popen([str(EXE), folder, folder, '--diagnose', '--owner-pid=' + str(owner.pid)],
                    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, creationflags=subprocess.CREATE_NO_WINDOW)
                lines = queue.Queue()
                def read():
                    for line in router.stdout:
                        lines.put(json.loads(line.decode('utf-8')))
                threading.Thread(target=read, daemon=True).start()
                first = lines.get(timeout=10)
                self.assertEqual(first['type'], 'status')
                self.assertIsNone(router.poll())
                if close_stdin:
                    router.stdin.close()
                else:
                    owner.terminate(); owner.wait(timeout=5)
                    self.assertFalse(router.stdin.closed, 'stdin stays open to reproduce the orphan bug')
                self.assertEqual(router.wait(timeout=10), 0)
                records = []
                while not lines.empty(): records.append(lines.get())
                self.assertTrue(any(r.get('phase') == '已退出' for r in records), records)
            finally:
                if router and router.poll() is None: router.terminate(); router.wait(timeout=5)
                if router:
                    for stream in (router.stdin, router.stdout, router.stderr):
                        if stream and not stream.closed: stream.close()
                if owner.poll() is None: owner.terminate(); owner.wait(timeout=5)

    def test_owner_death_exits_even_with_stdin_held_open(self):
        self.exercise(False)

    def test_normal_pipe_close_still_exits(self):
        self.exercise(True)

    def test_invalid_owner_is_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            p = subprocess.run([str(EXE), folder, folder, '--diagnose', '--owner-pid=0'], input=b'', capture_output=True, timeout=10)
            self.assertEqual(p.returncode, 1)
            self.assertIn('所属进程参数无效', p.stdout.decode('utf-8'))


if __name__ == '__main__': unittest.main()
