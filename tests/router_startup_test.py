import asyncio
import importlib.util
from pathlib import Path
import unittest
from unittest.mock import AsyncMock, patch

spec = importlib.util.spec_from_file_location('router_startup_service', Path(__file__).resolve().parents[1] / 'plugin/claw_service.py')
service = importlib.util.module_from_spec(spec); spec.loader.exec_module(service)


class Process:
    def __init__(self, code):
        self.returncode = code
        self.stdout = type('Stream', (), {'read': AsyncMock(return_value=b'{"error":"already running"}')})()

    async def wait(self):
        if self.returncode is None: await asyncio.Event().wait()
        return self.returncode


class StartupTests(unittest.IsolatedAsyncioTestCase):
    async def test_duplicate_owner_is_retried_before_returning_a_live_child(self):
        healthy = Process(None)
        with patch.object(service.asyncio, 'create_subprocess_exec', AsyncMock(side_effect=[Process(2), healthy])) as create, \
             patch.object(service.asyncio, 'sleep', AsyncMock()) as sleep:
            actual = await service.start_router('router.exe', 'data', 'config')
        self.assertIs(actual, healthy)
        self.assertEqual(create.await_count, 2)
        self.assertTrue(create.call_args.args[-1].startswith('--owner-pid='))
        sleep.assert_awaited_once()

    async def test_mutex_conflict_has_a_bounded_failure(self):
        with patch.object(service.asyncio, 'create_subprocess_exec', AsyncMock(side_effect=lambda *a, **k: Process(2))) as create, \
             patch.object(service.asyncio, 'sleep', AsyncMock()):
            with self.assertRaisesRegex(RuntimeError, 'already running'):
                await service.start_router('router.exe', 'data', 'config')
        self.assertEqual(create.await_count, 12)

    async def test_an_exited_process_is_never_accepted_as_a_working_listener(self):
        with patch.object(service.asyncio, 'create_subprocess_exec', AsyncMock(return_value=Process(1))) as create:
            with self.assertRaisesRegex(RuntimeError, '退出码 1'):
                await service.start_router('router.exe', 'data', 'config')
        self.assertEqual(create.await_count, 1)


if __name__ == '__main__': unittest.main()
