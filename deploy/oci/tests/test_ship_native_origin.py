"""Offline CHAT native-origin transport tests; no fcntl, process, keys, or remote services."""
import ast
import copy
import importlib.util
import json
from pathlib import Path
import unittest
from unittest.mock import patch


HERE = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('native_origin_ship', HERE / 'ship.py')
ship = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ship)
NATIVE = 'https://native.fixture.invalid'
BROWSER = 'https://browser.fixture.invalid/'
PRIVATE_MARKER = 'SYNTHETIC_PRIVATE_MARKER'


def config(service='chat', **changes):
    value = {'service': service, 'browser_origin': BROWSER,
             'public_url': 'https://192.0.2.30:8443',
             'opaque': 'literal=$\'"+/\\=tail  ', 'nested': {'password': PRIVATE_MARKER}}
    value.update(changes)
    return value


class NativeOriginTests(unittest.TestCase):
    def test_canonical_helper_matches_deploy_ast_without_importing_unix_deployer(self):
        # 준비: deploy.py는 실행하지 않고 두 순수 함수의 AST만 읽는다.
        definitions = []
        for path in (HERE / 'deploy.py', HERE / 'ship.py'):
            nodes = [node for node in ast.parse(path.read_bytes().decode('utf-8')).body
                     if isinstance(node, ast.FunctionDef) and node.name == 'is_canonical_native_origin']
            self.assertEqual(len(nodes), 1)
            definitions.append(ast.dump(nodes[0]))

        # 검증: runner와 host가 같은 canonical origin 계약을 유지한다.
        self.assertEqual(definitions[0], definitions[1])

    def test_workflow_passes_one_optional_secret_as_environment_data(self):
        # 준비 / 실행: GitHub Secret 표현식은 shell/python 원문에 삽입하지 않는다.
        workflow = HERE.parents[1] / '.github/workflows/deploy.yaml'
        source = workflow.read_bytes().decode('utf-8')

        # 검증: 지정 deploy step의 환경 변수 하나만 추가한다.
        self.assertEqual(source.count('${{ secrets.CHAT_NATIVE_ORIGIN }}'), 1)
        self.assertIn('          CHAT_NATIVE_ORIGIN: ${{ secrets.CHAT_NATIVE_ORIGIN }}', source)
        self.assertIn('run: python3 deploy/oci/ship.py --service chat --image "$RELEASE_IMAGE"', source)

    def test_empty_optional_secret_preserves_absent_field_and_all_existing_data(self):
        # 준비
        original = config()
        before = copy.deepcopy(original)

        # 실행
        result = ship.apply_native_origin(original, '')

        # 검증: 빈 입력으로 필드를 만들거나 다른 값을 재구성하지 않는다.
        self.assertIs(result, original)
        self.assertEqual(original, before)
        self.assertNotIn('native_origin', result)

    def test_empty_optional_secret_keeps_valid_existing_native_origin(self):
        original = config(native_origin=NATIVE)
        result = ship.apply_native_origin(original, '')
        self.assertIs(result, original)
        self.assertEqual(result['native_origin'], NATIVE)
        self.assertEqual(result['browser_origin'], BROWSER)

    def test_new_native_secret_copies_only_the_native_field(self):
        # 준비
        original = config()
        before = copy.deepcopy(original)

        # 실행
        result = ship.apply_native_origin(original, NATIVE)

        # 검증: 원본 config와 nested 필드의 내용·browser 원문을 변경하지 않는다.
        self.assertIsNot(result, original)
        self.assertEqual(original, before)
        self.assertEqual(result, {**before, 'native_origin': NATIVE})
        self.assertEqual(result['browser_origin'], BROWSER)

    def test_equal_existing_field_and_secret_are_idempotent(self):
        original = config(native_origin=NATIVE)
        before = copy.deepcopy(original)
        first = ship.apply_native_origin(original, NATIVE)
        second = ship.apply_native_origin(first, NATIVE)
        self.assertEqual(first, before)
        self.assertEqual(second, before)
        self.assertEqual(original, before)

    def test_conflicting_existing_field_and_secret_fail_without_value_reflection(self):
        original = config(native_origin=NATIVE)
        before = copy.deepcopy(original)
        with self.assertRaisesRegex(RuntimeError, '^NATIVE_ORIGIN_CONFLICT$') as error:
            ship.apply_native_origin(original, 'https://other.fixture.invalid')
        self.assertEqual(original, before)
        self.assertNotIn(NATIVE, str(error.exception))
        self.assertNotIn(PRIVATE_MARKER, str(error.exception))

    def test_explicit_invalid_existing_field_is_not_repaired_by_optional_secret(self):
        for existing in ('', None, ' ', '\t', '\n', False, 0, [], {}, NATIVE + '/', NATIVE.upper()):
            for optional in ('', NATIVE):
                with self.subTest(value_type=type(existing).__name__, supplied=bool(optional)):
                    original = config(native_origin=existing)
                    before = copy.deepcopy(original)
                    with self.assertRaisesRegex(RuntimeError, '^INVALID_NATIVE_ORIGIN$'):
                        ship.apply_native_origin(original, optional)
                    self.assertEqual(original, before)

    def test_browser_duplicate_is_rejected_without_normalizing_existing_browser(self):
        for browser in (NATIVE, NATIVE + '/'):
            for existing, optional in (({}, NATIVE), ({'native_origin': NATIVE}, '')):
                with self.subTest(browser_has_slash=browser.endswith('/'), has_existing=bool(existing)):
                    original = config(browser_origin=browser, **existing)
                    before = copy.deepcopy(original)
                    with self.assertRaisesRegex(RuntimeError, '^NATIVE_ORIGIN_DUPLICATES_BROWSER$'):
                        ship.apply_native_origin(original, optional)
                    self.assertEqual(original, before)

    def test_missing_or_nonstring_browser_is_left_for_existing_host_validation(self):
        for browser in (None, 0, [], {}):
            with self.subTest(value_type=type(browser).__name__):
                self.assertEqual(ship.apply_native_origin(config(browser_origin=browser), NATIVE)['native_origin'], NATIVE)

    def test_ll_ignores_optional_chat_secret_and_preserves_its_original_object(self):
        for existing in ({}, {'native_origin': None}, {'native_origin': 'legacy-value'}):
            for optional in ('', NATIVE, PRIVATE_MARKER, None):
                with self.subTest(existing=bool(existing), supplied=bool(optional)):
                    original = config('ll', **existing)
                    before = copy.deepcopy(original)
                    self.assertIs(ship.apply_native_origin(original, optional), original)
                    self.assertEqual(original, before)

    def test_canonical_dns_ipv4_and_compressed_bracketed_ipv6_are_accepted(self):
        for value in (NATIVE, NATIVE + ':8443', 'https://192.0.2.20', 'https://192.0.2.20:8443',
                      'https://[2001:db8::20]', 'https://[2001:db8::20]:8443'):
            with self.subTest(value_type='synthetic-origin'):
                self.assertTrue(ship.is_canonical_native_origin(value))
                self.assertEqual(ship.apply_native_origin(config(), value)['native_origin'], value)

    def test_noncanonical_or_multiple_origin_inputs_are_rejected(self):
        invalid = (None, 0, False, [], {}, '', ' ', NATIVE + ' ', ' ' + NATIVE,
                   NATIVE + '\n', NATIVE + '\r', NATIVE + '\t', NATIVE + '\0', NATIVE + '\x7f',
                   NATIVE + '\u00a0', NATIVE + '\u200b', NATIVE + ',' + NATIVE,
                   'http://native.fixture.invalid', 'HTTPS://native.fixture.invalid',
                   'https://NATIVE.fixture.invalid', 'https://native.fixture.invalid.',
                   'https://*.fixture.invalid', NATIVE + '/', NATIVE + '/path', NATIVE + ';param',
                   NATIVE + '?q=1', NATIVE + '?', NATIVE + '#value', NATIVE + '#',
                   'https://user:' + PRIVATE_MARKER + '@native.fixture.invalid',
                   NATIVE + ':443', NATIVE + ':0', NATIVE + ':08443', NATIVE + ':65536', NATIVE + ':',
                   'https://192.000.2.20', 'https://127.1', 'https://0xc0000214',
                   'https://[2001:DB8::20]', 'https://[2001:0db8::20]', 'https://2001:db8::20',
                   'https://[fe80::20%25eth0]', 'https://한글.fixture.invalid',
                   'https://native%2efixture.invalid', 'https://native', 'https://native_name.fixture.invalid',
                   'https://-native.fixture.invalid', 'https://native-.fixture.invalid', 'https://native.123')
        for value in invalid:
            with self.subTest(value_type=type(value).__name__):
                self.assertFalse(ship.is_canonical_native_origin(value))
                if value != '':
                    with self.assertRaisesRegex(RuntimeError, '^INVALID_CHAT_NATIVE_ORIGIN$') as error:
                        ship.apply_native_origin(config(), value)
                    self.assertNotIn(PRIVATE_MARKER, str(error.exception))

    def test_main_rejects_bad_overlay_before_key_materialization_or_transport(self):
        # 준비: 원격/키 환경은 아예 제공하지 않는다. TemporaryDirectory도 실제 파일을 만들지 않는다.
        base = {'GITHUB_SHA': 'a' * 40, 'GITHUB_RUN_ID': '123', 'GITHUB_RUN_ATTEMPT': '1',
                'OCI_HOST': '192.0.2.30', 'RUNTIME_CONFIG_JSON': json.dumps(config()),
                'CHAT_NATIVE_ORIGIN': NATIVE + '/path'}
        argv = ['ship.py', '--service', 'chat', '--image', 'ghcr.io/fixture/chat@sha256:' + 'b' * 64]
        with patch.object(ship.os, 'environ', base), patch.object(ship.sys, 'argv', argv), \
                patch.object(ship.tempfile, 'TemporaryDirectory') as directory, \
                patch.object(ship.os, 'chmod'), patch.object(Path, 'write_text') as write, \
                patch.object(ship, 'checked') as transport:
            directory.return_value.__enter__.return_value = 'synthetic-directory-never-created'

            # 실행 / 검증: config를 쓰거나 SSH 관련 환경을 읽기 전에 고정 오류로 끝난다.
            with self.assertRaisesRegex(RuntimeError, '^INVALID_CHAT_NATIVE_ORIGIN$'):
                ship.main()
            write.assert_not_called()
            transport.assert_not_called()

    def test_main_reads_optional_secret_only_inside_the_chat_service_branch(self):
        # 준비 / 실행: LL의 공용 ship 경로가 CHAT Secret을 새 의존성으로 갖지 않게 한다.
        tree = ast.parse((HERE / 'ship.py').read_bytes().decode('utf-8'))
        main = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == 'main')
        branches = [node for node in ast.walk(main) if isinstance(node, ast.If)
                    and ast.dump(node.test) == ast.dump(ast.parse("a.service == 'chat'", mode='eval').body)]

        # 검증: 선택 값 읽기와 순수 overlay 호출이 해당 branch에 한 번만 있다.
        self.assertEqual(len(branches), 1)
        branch = branches[0]
        secret_reads = [node for node in ast.walk(branch) if isinstance(node, ast.Constant) and node.value == 'CHAT_NATIVE_ORIGIN']
        merge_calls = [node for node in ast.walk(branch) if isinstance(node, ast.Call)
                       and isinstance(node.func, ast.Name) and node.func.id == 'apply_native_origin']
        self.assertEqual(len(secret_reads), 1)
        self.assertEqual(len(merge_calls), 1)


if __name__ == '__main__':
    unittest.main(verbosity=2)
