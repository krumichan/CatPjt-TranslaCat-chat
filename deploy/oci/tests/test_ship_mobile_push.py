"""Public Actions push inputs -> ship JSON -> runtime.env, using synthetic data only."""
import ast
import copy
import json
import os
from pathlib import Path
from types import SimpleNamespace
import tempfile
import unittest
from unittest.mock import patch

from test_native_origin_config import fixture_config, source_namespace
from test_ship_native_origin import ship

HERE=Path(__file__).resolve().parents[1]
PROJECT='00000000-0000-4000-8000-000000000001'
PRIVATE='SYNTHETIC_EXISTING_PRIVATE_MARKER'
PUBLIC={'enabled':True,'app_environment':'development','project_id':PROJECT}


class ShipMobilePushTests(unittest.TestCase):
    def test_unset_and_ll_inputs_return_original_object_and_preserve_every_existing_value(self):
        # 준비 / 실행 / 검증
        original=fixture_config('chat'); original['native_origin']='https://native.fixture.invalid'
        before=copy.deepcopy(original)
        self.assertIs(ship.apply_mobile_push(original,'','',''),original)
        self.assertEqual(original,before)
        for inputs in (('','',''),('true','development',PROJECT),(PRIVATE,None,{})):
            ll=fixture_config('ll'); ll['mobile_push']='legacy-value'
            self.assertIs(ship.apply_mobile_push(ll,*inputs),ll)

    def test_public_settings_add_only_mobile_push_without_mutating_config_or_origin(self):
        # 준비
        original=fixture_config('chat');original['native_origin']='https://native.fixture.invalid'
        original['nested']={'private':PRIVATE}; before=copy.deepcopy(original)

        # 실행
        updated=ship.apply_mobile_push(original,'true','development',PROJECT)

        # 검증
        self.assertEqual(updated,{**before,'mobile_push':PUBLIC})
        self.assertEqual(original,before)
        self.assertEqual(updated['browser_origin'],before['browser_origin'])
        self.assertEqual(updated['native_origin'],before['native_origin'])

    def test_explicit_false_disables_existing_true_and_explicit_true_enables_existing_false(self):
        # 준비 / 실행 / 검증: boolean switch는 승인된 공개 override이고 환경/프로젝트는 유지한다.
        for enabled in ('true','false'):
            existing=fixture_config('chat');existing['mobile_push']={**PUBLIC,'enabled':enabled!='true'}
            before=copy.deepcopy(existing)
            result=ship.apply_mobile_push(existing,enabled,'development',PROJECT)
            self.assertEqual(result['mobile_push'],{**PUBLIC,'enabled':enabled=='true'})
            self.assertEqual(existing,before)

    def test_partial_public_settings_are_rejected(self):
        # 준비 / 실행 / 검증: 일부 값만 바꿔 잘못된 프로젝트를 선택하지 않는다.
        values=('true','development',PROJECT)
        for mask in range(1,7):
            inputs=tuple(value if mask & (1<<index) else '' for index,value in enumerate(values))
            with self.subTest(mask=mask),self.assertRaises(RuntimeError):
                ship.apply_mobile_push(fixture_config('chat'),*inputs)

    def test_invalid_inputs_and_existing_values_fail_without_private_value_reflection(self):
        # 준비
        invalid=[(value,'development',PROJECT) for value in (None,False,0,'True','1',' '+PRIVATE)]
        invalid += [('true',value,PROJECT) for value in ('Development','staging',PRIVATE+'\nINJECT=1',None)]
        invalid += [('true','development',value) for value in (None,0,PRIVATE,PROJECT+'\n',PROJECT.upper().replace('4000','A000'))]
        for index,inputs in enumerate(invalid):
            with self.subTest(index=index),self.assertRaises(RuntimeError) as error:
                ship.apply_mobile_push(fixture_config('chat'),*inputs)
            self.assertNotIn(PRIVATE,str(error.exception))
        for existing in (None,[],{'enabled':'true'},{'enabled':True}, {'enabled':False,'expo_access_token':PRIVATE}):
            with self.subTest(existing_type=type(existing).__name__):
                value=fixture_config('chat');value['mobile_push']=existing
                with self.assertRaisesRegex(RuntimeError,'^INVALID_EXISTING_CHAT_MOBILE_PUSH$') as error:
                    ship.apply_mobile_push(value,'true','development',PROJECT)
                self.assertNotIn(PRIVATE,str(error.exception))

    def test_conflicting_project_or_environment_fails_closed(self):
        # 준비 / 실행 / 검증
        for existing in ({**PUBLIC,'app_environment':'production'},{**PUBLIC,'project_id':PROJECT[:-1]+'2'}):
            value=fixture_config('chat');value['mobile_push']=existing
            with self.assertRaisesRegex(RuntimeError,'^CHAT_MOBILE_PUSH_CONFIG_CONFLICT$'):
                ship.apply_mobile_push(value,'false','development',PROJECT)

    def test_materialized_config_exports_exact_public_env_and_preserves_credentials_migration_and_no_flag_bytes(self):
        # 준비: 실제 ship overlay와 기존 serializer 함수 본문을 함께 사용한다.
        subject=source_namespace()
        subject['Path']=lambda path: SimpleNamespace(read_text=lambda:'SYNTHETIC_PUBLIC_ROOTS')
        subject['os']=SimpleNamespace(geteuid=lambda:1000,chmod=os.chmod)
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory); before=fixture_config('chat')
            before['native_origin']='https://native.fixture.invalid'
            absent=ship.apply_mobile_push(before,'','','')
            updated=ship.apply_mobile_push(before,'true','development',PROJECT)
            for name in ('before','absent','updated'): (root/name).mkdir()

            # 실행
            old=subject['materialize'](before,root/'before')
            no_flag=subject['materialize'](absent,root/'absent')
            new=subject['materialize'](updated,root/'updated')
            old_migration=subject['materialize'](before,root/'before',True)
            new_migration=subject['materialize'](updated,root/'updated',True)

            # 검증: unset은 전체 파일 byte가 같고 enabled는 공개 세 행만 추가한다.
            for file in old.iterdir(): self.assertEqual(file.read_bytes(),(no_flag/file.name).read_bytes())
            actual=(new/'runtime.env').read_text()
            for record in ('Chat__Push__Enabled=true\n','Chat__Push__AppEnvironment=development\n','Chat__Push__ProjectId='+PROJECT+'\n'):
                self.assertEqual(actual.count(record),1);actual=actual.replace(record,'')
            self.assertEqual(actual,(old/'runtime.env').read_text())
            self.assertEqual((old_migration/'runtime.env').read_bytes(),(new_migration/'runtime.env').read_bytes())
            self.assertNotIn('Chat__Push__',(new_migration/'runtime.env').read_text())
            for file in old.iterdir():
                if file.name!='runtime.env': self.assertEqual(file.read_bytes(),(new/file.name).read_bytes())

    def test_invalid_public_input_fails_before_any_runtime_key_write_or_transport(self):
        # 준비: 비밀키/SSH 환경을 제공하지 않아도 순수 설정 오류가 먼저 나와야 한다.
        env={'GITHUB_SHA':'a'*40,'GITHUB_RUN_ID':'1','GITHUB_RUN_ATTEMPT':'1','OCI_HOST':'192.0.2.30',
             'RUNTIME_CONFIG_JSON':json.dumps({'service':'chat','public_url':'https://192.0.2.30:8443'}),
             'CHAT_MOBILE_PUSH_ENABLED':'true','CHAT_MOBILE_PUSH_APP_ENVIRONMENT':'development'}
        argv=['ship.py','--service','chat','--image','ghcr.io/fixture/chat@sha256:'+'b'*64]
        with patch.object(ship.os,'environ',env),patch.object(ship.sys,'argv',argv),patch.object(ship.tempfile,'TemporaryDirectory') as directory,\
             patch.object(ship.os,'chmod'),patch.object(Path,'write_text') as write,patch.object(ship,'checked') as transport:
            directory.return_value.__enter__.return_value='synthetic-directory-not-created'

            # 실행 / 검증
            with self.assertRaisesRegex(RuntimeError,'^INVALID_CHAT_MOBILE_PUSH_PROJECT$'): ship.main()
            write.assert_not_called();transport.assert_not_called()

    def test_main_consumes_public_settings_before_serializing_runtime_json(self):
        # 준비: 첫 runtime.json 쓰기 직전에서 중단하여 키 접근/원격 호출 없이 실제 main의 전달 값을 확인한다.
        class Captured(RuntimeError): pass
        env={'GITHUB_SHA':'a'*40,'GITHUB_RUN_ID':'1','GITHUB_RUN_ATTEMPT':'1','OCI_HOST':'192.0.2.30','GITHUB_ACTOR':'synthetic-owner',
             'RUNTIME_CONFIG_JSON':json.dumps({'service':'chat','public_url':'https://192.0.2.30:8443','nested':{'private':PRIVATE}}),
             'CHAT_MOBILE_PUSH_ENABLED':'true','CHAT_MOBILE_PUSH_APP_ENVIRONMENT':'development','CHAT_MOBILE_PUSH_PROJECT_ID':PROJECT}
        argv=['ship.py','--service','chat','--image','ghcr.io/fixture/chat@sha256:'+'b'*64]
        with patch.object(ship.os,'environ',env),patch.object(ship.sys,'argv',argv),patch.object(ship.tempfile,'TemporaryDirectory') as directory,\
             patch.object(ship.os,'chmod'),patch.object(Path,'write_text',side_effect=Captured) as write,patch.object(ship,'checked') as transport:
            directory.return_value.__enter__.return_value='synthetic-directory-not-created'

            # 실행
            with self.assertRaises(Captured): ship.main()

            # 검증
            captured=json.loads(write.call_args.args[0]);self.assertEqual(captured['mobile_push'],PUBLIC)
            self.assertEqual(captured['nested'],{'private':PRIVATE});transport.assert_not_called()

    def test_workflow_uses_public_variables_once_and_reads_them_only_in_chat_branch(self):
        # 준비 / 실행
        workflow=(HERE.parents[1]/'.github/workflows/deploy.yaml').read_text(encoding='utf-8')
        tree=ast.parse((HERE/'ship.py').read_text(encoding='utf-8'))
        main=next(node for node in tree.body if isinstance(node,ast.FunctionDef) and node.name=='main')
        chat=next(node for node in ast.walk(main) if isinstance(node,ast.If) and ast.dump(node.test)==ast.dump(ast.parse("a.service=='chat'",mode='eval').body))

        # 검증: optional 값은 shell문에 삽입하거나 secret blob에 새로 저장하지 않는다.
        for name in ('CHAT_MOBILE_PUSH_ENABLED','CHAT_MOBILE_PUSH_APP_ENVIRONMENT','CHAT_MOBILE_PUSH_PROJECT_ID'):
            self.assertEqual(workflow.count('${{ vars.'+name+' }}'),1)
            self.assertIn('          '+name+': ${{ vars.'+name+' }}',workflow)
            self.assertNotIn('${{ secrets.'+name+' }}',workflow)
            self.assertEqual(sum(isinstance(node,ast.Constant) and node.value==name for node in ast.walk(chat)),1)
        self.assertEqual(sum(isinstance(node,ast.Call) and isinstance(node.func,ast.Name) and node.func.id=='apply_mobile_push' for node in ast.walk(chat)),1)


if __name__=='__main__': unittest.main()
