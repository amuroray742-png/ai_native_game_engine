import unittest

from verify_unity_assets import is_expected_injection


class InjectedFailureTests(unittest.TestCase):
    def test_inner_injection_does_not_mask_cleanup_failure(self):
        injected = "System.InvalidOperationException: Injected AssetsDemo failure at after-start"
        for outer in ("System.AggregateException: Close failed",
                      "System.InvalidOperationException: Resources remain after shutdown."):
            with self.subTest(outer=outer):
                result = {"success": False, "message": outer + " ---> " + injected}
                self.assertFalse(is_expected_injection(result, "after-start"))
