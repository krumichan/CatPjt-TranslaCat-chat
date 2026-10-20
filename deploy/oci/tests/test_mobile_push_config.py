"""공개 push rollout serializer의 합성 검증. 운영 설정·키·네트워크·DB는 읽지 않는다."""
import copy
import os
from pathlib import Path
from types import SimpleNamespace
import tempfile
import unittest

from test_native_origin_config import fixture_config, source_namespace
from test_ship_native_origin import ship

HERE=Path(__file__).resolve().parents[1]
PROJECT='00000000-0000-4000-8000-000000000001'
MARKER='SYNTHETIC_PRIVATE_MARKER_NEVER_REFLECT'


class MobilePushConfigTests(unittest.TestCase):
    def setUp(self):
        self.subject=source_namespace()

    def test_missing_and_empty_config_preserve_disabled_default(self):
        # 준비 / 실행 / 검증
        self.assertIsNone(self.subject['mobile_push']({}))
        self.assertEqual(self.subject['mobile_push']({'mobile_push':{}}),{'enabled':False})
        self.assertEqual(self.subject['mobile_push']({'mobile_push':{'enabled':False}}),{'enabled':False})

    def test_enabled_config_requires_exact_environment_and_canonical_public_project(self):
        # 준비 / 실행 / 검증
        for environment in ('development','preview','production'):
            value={'enabled':True,'app_environment':environment,'project_id':PROJECT}
            self.assertEqual(self.subject['mobile_push']({'mobile_push':value}),value)

    def test_nonboolean_enabled_is_rejected_without_input_reflection(self):
        for value in (None,0,1,'false','true',MARKER,[],{}):
            with self.subTest(value_type=type(value).__name__):
                with self.assertRaisesRegex(self.subject['DeployError'],'^INVALID_MOBILE_PUSH_ENABLED$') as error:
                    self.subject['mobile_push']({'mobile_push':{'enabled':value}})
                self.assertNotIn(MARKER,str(error.exception))

    def test_invalid_shape_environment_project_and_unknown_secret_field_are_rejected(self):
        # 준비
        invalid=[None,False,0,'',[],{'enabled':True},
                 {'enabled':False,'expo_access_token':MARKER}]
        invalid.extend({'enabled':True,'app_environment':value,'project_id':PROJECT}
                       for value in (None,False,0,'Development','staging',MARKER+'\nINJECT=1'))
        invalid.extend({'enabled':True,'app_environment':'development','project_id':value}
                       for value in (None,False,0,'',PROJECT.upper().replace('4000','A000'),'not-a-uuid',MARKER))
        for index,value in enumerate(invalid):
            with self.subTest(index=index):
                with self.assertRaises(self.subject['DeployError']) as error:
                    self.subject['mobile_push']({'mobile_push':value})
                self.assertNotIn(MARKER,str(error.exception))

    def test_materialize_only_adds_public_runtime_values_preserves_credentials_and_migration(self):
        # 준비: 공개 root CA와 Unix ownership만 합성 대역으로 대체한다.
        subject=self.subject
        subject['Path']=lambda path: SimpleNamespace(read_text=lambda:'SYNTHETIC_PUBLIC_ROOTS')
        subject['os']=SimpleNamespace(geteuid=lambda:1000,chmod=os.chmod)
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory)
            for service in ('chat','ll'):
                before=fixture_config(service); after=copy.deepcopy(before)
                after['mobile_push']={'enabled':True,'app_environment':'development','project_id':PROJECT}
                (root/service/'before').mkdir(parents=True); (root/service/'after').mkdir(parents=True)

                # 실행
                old=subject['materialize'](before,root/service/'before')
                new=subject['materialize'](after,root/service/'after')
                old_migration=subject['materialize'](before,root/service/'before',True)
                new_migration=subject['materialize'](after,root/service/'after',True)
                actual=(new/'runtime.env').read_text()

                # 검증: 공개 세 행 외의 기존 env·비밀 파일·migration·LL 출력은 그대로다.
                records=['Chat__Push__Enabled=true\n','Chat__Push__AppEnvironment=development\n','Chat__Push__ProjectId='+PROJECT+'\n']
                stripped=actual
                for record in records:
                    self.assertEqual(actual.count(record),1 if service=='chat' else 0)
                    stripped=stripped.replace(record,'')
                self.assertEqual(stripped,(old/'runtime.env').read_text())
                self.assertNotIn('ExpoAccessToken',actual)
                if service=='chat':
                    self.assertNotIn(before['database']['password'],actual)
                    self.assertNotIn(before['ai_api_key'],actual)
                self.assertEqual((old_migration/'runtime.env').read_bytes(),(new_migration/'runtime.env').read_bytes())
                self.assertNotIn('Chat__Push__',(new_migration/'runtime.env').read_text())
                for file in old.iterdir():
                    if file.name!='runtime.env': self.assertEqual(file.read_bytes(),(new/file.name).read_bytes())

    def test_explicit_disabled_is_serialized_as_lowercase_false(self):
        # 준비
        subject=self.subject
        subject['Path']=lambda path: SimpleNamespace(read_text=lambda:'SYNTHETIC_PUBLIC_ROOTS')
        subject['os']=SimpleNamespace(geteuid=lambda:1000,chmod=os.chmod)
        value=fixture_config('chat');value['mobile_push']={'enabled':False}
        with tempfile.TemporaryDirectory() as directory:
            # 실행 / 검증
            result=subject['materialize'](value,Path(directory))
            self.assertIn('Chat__Push__Enabled=false\n',(result/'runtime.env').read_text())
            self.assertNotIn('Chat__Push__AppEnvironment=',(result/'runtime.env').read_text())

    def test_ship_preserves_optional_mobile_push_and_existing_native_origin(self):
        # 준비
        value={'service':'chat','browser_origin':'https://web.fixture.invalid',
               'native_origin':'https://native.fixture.invalid','mobile_push':{'enabled':True,'app_environment':'development','project_id':PROJECT},
               'nested':{'password':MARKER}}
        before=copy.deepcopy(value)

        # 실행 / 검증: ship는 이미 전체 config 객체를 전달하므로 별도 secret/overlay를 추가하지 않는다.
        self.assertEqual(ship.apply_native_origin(value,''),before)
        self.assertEqual(value,before)

    def test_ci_discovers_tests_and_migration_returns_before_optional_worker_composition(self):
        # 준비 / 실행
        workflow=(HERE.parents[1]/'.github/workflows/deploy.yaml').read_text(encoding='utf-8')
        program=(HERE.parents[1]/'TranslaCat.Chat.Api/Program.cs').read_text(encoding='utf-8')
        readiness=(HERE.parents[1]/'TranslaCat.Chat.Api/Runtime/ChatReadiness.cs').read_text(encoding='utf-8')

        # 검증: 기존 CI discovery에 포함되며 DB pending migration gate를 보존한다.
        self.assertIn('python3 -m unittest discover -s deploy/oci/tests -v',workflow)
        self.assertLess(program.index('--migrate-database'),program.index('builder.Services.AddChatPush'))
        self.assertIn('GetPendingMigrationsAsync',readiness)


if __name__=='__main__':
    unittest.main()
