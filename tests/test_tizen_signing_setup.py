"""Secret-handling checks: no real keys, network requests, or repository writes."""
import base64
import contextlib
import importlib.util
import io
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('signing_setup', Path(__file__).resolve().parents[1] / 'scripts/configure-tizen-signing.py')
setup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(setup)


class SigningSetupTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='streamed-signing-setup-test-')
        self.addCleanup(temporary.cleanup)
        self.author, self.distributor = [Path(temporary.name) / name for name in ['author.p12', 'distributor.p12']]
        self.author.write_bytes(b'author-test-bytes')
        self.distributor.write_bytes(b'distributor-test-bytes')

    def test_passwords_and_encoded_profiles_only_reach_secret_stdin(self):
        author_password, distributor_password = 'author-password-value', 'distributor-password-value'
        with patch.object(setup, 'run', return_value=subprocess.CompletedProcess([], 0, b'', b'')) as run, \
             patch.object(setup, 'validate_profile', return_value='2030-01-01'), \
             contextlib.redirect_stdout(io.StringIO()) as output:
            setup.configure(self.author, self.distributor, password_prompt=unittest.mock.Mock(side_effect=[author_password, distributor_password]))
        uploads = [call for call in run.call_args_list if call.args[0][:3] == ['gh', 'secret', 'set']]
        self.assertEqual(len(uploads), 4)
        self.assertEqual([call.kwargs['data'] for call in uploads], [base64.b64encode(self.author.read_bytes()), author_password.encode(),
                         base64.b64encode(self.distributor.read_bytes()), distributor_password.encode()])
        for secret in [author_password, distributor_password, base64.b64encode(self.author.read_bytes()).decode(), base64.b64encode(self.distributor.read_bytes()).decode()]:
            self.assertNotIn(secret, output.getvalue())
            self.assertNotIn(secret, repr([call.args[0] for call in run.call_args_list]))
        self.assertEqual(run.call_args_list[-1].args[0][:4], ['gh', 'workflow', 'run', 'vendor-tv.yml'])

    def test_invalid_profile_causes_no_secret_writes(self):
        with patch.object(setup, 'run', return_value=subprocess.CompletedProcess([], 0, b'', b'')) as run, \
             patch.object(setup, 'validate_profile', side_effect=setup.SetupError('Expired certificate')):
            with self.assertRaisesRegex(setup.SetupError, 'Expired certificate'):
                setup.configure(self.author, self.distributor, password_prompt=lambda _: 'password')
        self.assertEqual(len(run.call_args_list), 1)

    def test_inaccessible_secrets_stop_before_password_prompt(self):
        prompt = unittest.mock.Mock()
        with patch.object(setup, 'run', return_value=subprocess.CompletedProcess([], 1, b'', b'permission denied')):
            with self.assertRaisesRegex(setup.SetupError, 'Cannot access repository secrets'):
                setup.configure(self.author, self.distributor, password_prompt=prompt)
        prompt.assert_not_called()

    def test_failed_upload_does_not_echo_child_errors_or_dispatch(self):
        def fake_run(arguments, **kwargs):
            code = 1 if arguments[:3] == ['gh', 'secret', 'set'] else 0
            return subprocess.CompletedProcess(arguments, code, b'', b'do not echo secret-password-value')
        with patch.object(setup, 'run', side_effect=fake_run) as run, \
             patch.object(setup, 'validate_profile', return_value='2030-01-01'), \
             contextlib.redirect_stdout(io.StringIO()) as output:
            with self.assertRaisesRegex(setup.SetupError, 'Some secrets may have changed') as error:
                setup.configure(self.author, self.distributor, password_prompt=lambda _: 'secret-password-value')
        self.assertNotIn('secret-password-value', output.getvalue() + str(error.exception))
        self.assertFalse(any(call.args[0][1:3] == ['workflow', 'run'] for call in run.call_args_list))

    def test_openssl_password_is_in_child_environment_and_keys_are_not_exported(self):
        exported = b'-----BEGIN CERTIFICATE-----\npublic\n-----END CERTIFICATE-----'
        dates = b'notBefore=Jan  1 00:00:00 2020 GMT\nnotAfter=Jan  1 00:00:00 2099 GMT\n'
        responses = [subprocess.CompletedProcess([], 0, exported, b''), subprocess.CompletedProcess([], 0, dates, b'')]
        with patch.object(setup, 'run', side_effect=responses) as run:
            self.assertEqual(setup.validate_profile(b'P12-data', 'password-value', 'Author'), '2099-01-01')
        call = run.call_args_list[0]
        self.assertIn('-nokeys', call.args[0])
        self.assertNotIn('password-value', repr(call.args[0]))
        self.assertEqual(call.kwargs['environment']['STREAMED_PROFILE_PASSWORD'], 'password-value')

    def test_expired_certificate_is_rejected(self):
        exported = b'-----BEGIN CERTIFICATE-----\npublic\n-----END CERTIFICATE-----'
        dates = b'notBefore=Jan  1 00:00:00 2012 GMT\nnotAfter=Jan  1 00:00:00 2019 GMT\n'
        with patch.object(setup, 'run', side_effect=[subprocess.CompletedProcess([], 0, exported, b''), subprocess.CompletedProcess([], 0, dates, b'')]):
            with self.assertRaisesRegex(setup.SetupError, 'expired or not-yet-valid'):
                setup.validate_profile(b'P12-data', 'password-value', 'Author')


if __name__ == '__main__':
    unittest.main()
