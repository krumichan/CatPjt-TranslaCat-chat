"""Public safety variables -> actual ship JSON -> actual runtime serializer; synthetic inputs only."""
import ast
import argparse
import copy
import io
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace
import tempfile
import unittest
from unittest.mock import Mock, patch

from test_native_origin_config import fixture_config, source_namespace
from test_ship_native_origin import ship

HERE=Path(__file__).resolve().parents[1]
PRIVATE='SYNTHETIC_PRIVATE_VALUE_NOT_FOR_DIAGNOSTICS'
FIELDS=('enabled','account_deletion_enabled')
ENV=('Chat__Safety__Enabled','Chat__Safety__AccountDeletionEnabled')


class SafetyFlagsTests(unittest.TestCase):
    def test_raw_json_rejects_duplicate_safety_only_and_keeps_other_last_wins_behavior(self):
        # 준비: dict로 바꾸기 전 원문에서 중복을 확인해야 동일 값의 중복도 거절된다.
        subject=source_namespace()
        invalid=(
            '{"service":"chat","safety":{},"safety":{}}',
            '{"service":"chat","safety":{"enabled":true,"enabled":true}}',
            '{"service":"chat","safety":{"account_deletion_enabled":false,"account_deletion_enabled":true}}',
            '{"service":"chat","safety":{"unknown":"'+PRIVATE+'","unknown":false}}',
        )
        for raw in invalid:
            for parser,error_type in ((ship.parse_runtime_config,RuntimeError),
                                      (subject['parse_runtime_config'],subject['DeployError'])):
                # 실행 / 검증: 고정 오류만 반환하고 입력 본문이나 개인 설정은 반사하지 않는다.
                with self.assertRaisesRegex(error_type,'^DUPLICATE_CHAT_SAFETY_JSON$') as error:
                    parser(raw)
                self.assertNotIn(PRIVATE,str(error.exception))

        # 검증: safety 이외의 기존 JSON 계약과 LL 설정에는 새로운 중복 제약을 추가하지 않는다.
        valid=(
            '{"service":"chat","opaque":1,"opaque":2,"nested":{"key":1,"key":2},"safety":{"enabled":false}}',
            '{"service":"chat","opaque":{"safety":{},"safety":{}},"safety":{}}',
            '{"service":"ll","safety":{},"safety":{"enabled":false,"enabled":true}}',
        )
        for raw in valid:
            expected=json.loads(raw)
            self.assertEqual(ship.parse_runtime_config(raw),expected)
            self.assertEqual(subject['parse_runtime_config'](raw),expected)

    def test_ship_cli_rejects_raw_duplicate_before_any_key_file_or_transport(self):
        # 준비 / 실행 / 검증: 실제 CLI가 strict parser를 거치며 dict helper에만 검사를 맡기지 않는다.
        raw='{"service":"chat","public_url":"https://192.0.2.30:8443","safety":{},"safety":{"enabled":true},"private":"'+PRIVATE+'"}'
        env={'GITHUB_SHA':'a'*40,'GITHUB_RUN_ID':'1','GITHUB_RUN_ATTEMPT':'1','OCI_HOST':'192.0.2.30',
             'RUNTIME_CONFIG_JSON':raw}
        argv=['ship.py','--service','chat','--image','ghcr.io/fixture/chat@sha256:'+'b'*64]
        with patch.object(ship.os,'environ',env),patch.object(ship.sys,'argv',argv),\
             patch.object(ship.tempfile,'TemporaryDirectory') as directory,patch.object(ship.os,'chmod'),\
             patch.object(Path,'write_text') as write,patch.object(ship,'checked') as transport:
            directory.return_value.__enter__.return_value='synthetic-directory-not-created'
            with self.assertRaisesRegex(RuntimeError,'^DUPLICATE_CHAT_SAFETY_JSON$') as error:ship.main()
            self.assertNotIn(PRIVATE,str(error.exception))
            write.assert_not_called();transport.assert_not_called()

    def test_deploy_cli_rejects_raw_duplicate_before_validation_or_deployment(self):
        # 준비: Unix import 없이 실제 main 본문을 실행하고 외부 실행 경계만 고정한다.
        subject=source_namespace()
        main=next(node for node in ast.parse((HERE/'deploy.py').read_text(encoding='utf-8')).body
                  if isinstance(node,ast.FunctionDef) and node.name=='main')
        subject.update(argparse=argparse,sys=sys,
                       signal=SimpleNamespace(SIGTERM=1,SIGHUP=2,SIGINT=3,signal=lambda *args:None),
                       validate=Mock(),deploy=Mock())
        exec(compile(ast.Module(body=[main],type_ignores=[]),str(HERE/'deploy.py'),'exec'),subject)
        cases=(
            '{"service":"chat","safety":{},"safety":{},"private":"'+PRIVATE+'"}',
            '{"service":"chat","safety":{"enabled":true,"enabled":false}}',
        )
        with tempfile.TemporaryDirectory() as directory:
            for index,raw in enumerate(cases):
                config=Path(directory)/(str(index)+'.json');config.write_text(raw,encoding='utf-8')
                argv=['deploy.py','--service','chat','--config',str(config),'--image','synthetic-image',
                      '--sha','a'*40,'--release-id','synthetic-release','--registry-token',str(Path(directory)/'unused-token')]

                # 실행
                with patch.object(sys,'argv',argv),patch.object(sys,'stderr',new_callable=io.StringIO) as stderr:
                    result=subject['main']()

                # 검증: 기존 일회성 config 정리는 유지하되 실제 배포나 비밀 파일 접근 전에 실패한다.
                self.assertEqual(result,1)
                self.assertEqual(stderr.getvalue(),'DEPLOYMENT_FAILED: DUPLICATE_CHAT_SAFETY_JSON\n')
                self.assertNotIn(PRIVATE,stderr.getvalue())
                self.assertFalse(config.exists())
                subject['validate'].assert_not_called();subject['deploy'].assert_not_called()

    def test_initial_missing_and_empty_config_are_both_false(self):
        # 준비 / 실행 / 검증: 공개 변수가 없는 기존 릴리스는 자동으로 기능을 켜지 않는다.
        subject=source_namespace()
        for extra in ({},{'safety':{}},{'safety':{'enabled':False}}):
            original={'service':'chat',**extra}
            before=copy.deepcopy(original)
            result=ship.apply_safety(original,'','')
            self.assertEqual(result['safety'],dict.fromkeys(FIELDS,False))
            self.assertEqual(subject['chat_safety'](result),dict.fromkeys(FIELDS,False))
            self.assertEqual(original,before)

    def test_all_four_public_combinations_are_independent_and_preserve_other_values(self):
        # 준비 / 실행 / 검증: false,true도 배포기의 별도 조합 제한으로 바꾸지 않는다.
        for enabled in ('true','false'):
            for deletion in ('true','false'):
                with self.subTest(enabled=enabled,deletion=deletion):
                    original=fixture_config('chat'); original['nested']={'private':PRIVATE}
                    original['safety']={'enabled':enabled!='true','account_deletion_enabled':deletion!='true'}
                    before=copy.deepcopy(original)
                    result=ship.apply_safety(original,enabled,deletion)
                    self.assertEqual(result,{**before,'safety':{'enabled':enabled=='true','account_deletion_enabled':deletion=='true'}})
                    self.assertEqual(original,before)

    def test_empty_public_values_preserve_each_existing_boolean_and_partial_override(self):
        # 준비 / 실행 / 검증
        for enabled in (True,False):
            for deletion in (True,False):
                original={'service':'chat','safety':dict(zip(FIELDS,(enabled,deletion)))}
                self.assertEqual(ship.apply_safety(original,'','')['safety'],original['safety'])
                self.assertEqual(ship.apply_safety(original,'false','')['safety'],dict(zip(FIELDS,(False,deletion))))
                self.assertEqual(ship.apply_safety(original,'','true')['safety'],dict(zip(FIELDS,(enabled,True))))

    def test_noncanonical_public_values_fail_without_reflecting_values(self):
        # 준비 / 실행 / 검증: strip/lower/truthiness로 잘못된 값이나 shell 문자열을 복구하지 않는다.
        invalid=(None,False,True,0,1,[],{},'True','FALSE','1','0',' true','false ','true\n','$(echo '+PRIVATE+')')
        for value in invalid:
            for index in (0,1):
                with self.subTest(type=type(value).__name__,field=index):
                    args=['',''];args[index]=value
                    with self.assertRaises(RuntimeError) as error: ship.apply_safety({'service':'chat'},*args)
                    self.assertNotIn(PRIVATE,str(error.exception))

    def test_invalid_existing_json_cannot_be_repaired_by_public_override(self):
        # 준비
        invalid=(None,False,0,'true',[],{'enabled':'true'},{'enabled':1},{'account_deletion_enabled':None},
                 {'enabled':False,'private_key':PRIVATE})
        subject=source_namespace()
        for value in invalid:
            original={'service':'chat','safety':value}
            for public in (('',''),('true','true'),('false','false')):
                with self.subTest(existing_type=type(value).__name__,public=public):
                    with self.assertRaisesRegex(RuntimeError,'^INVALID_EXISTING_CHAT_SAFETY$') as error:
                        ship.apply_safety(original,*public)
                    self.assertNotIn(PRIVATE,str(error.exception))
            with self.assertRaises(subject['DeployError']) as error: subject['chat_safety'](original)
            self.assertNotIn(PRIVATE,str(error.exception))

    def test_LL_does_not_consume_CHAT_flags_or_change_existing_config(self):
        # 준비 / 실행 / 검증
        original=fixture_config('ll');original['safety']=PRIVATE
        before=copy.deepcopy(original)
        self.assertIs(ship.apply_safety(original,PRIVATE,None),original)
        self.assertEqual(original,before)

    def test_actual_materialize_exports_both_flags_once_and_preserves_every_other_runtime_file(self):
        # 준비: 공개 CA 경로와 Unix ownership만 대체한다. serializer와 파일 작성은 실제 함수다.
        subject=source_namespace()
        subject['Path']=lambda path:SimpleNamespace(read_text=lambda:'SYNTHETIC_PUBLIC_ROOTS')
        subject['os']=SimpleNamespace(geteuid=lambda:1000,chmod=os.chmod)
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory)
            for enabled in ('true','false'):
                for deletion in ('true','false'):
                    original=fixture_config('chat')
                    updated=ship.apply_safety(original,enabled,deletion)
                    before=root/(enabled+deletion+'-before');after=root/(enabled+deletion+'-after')
                    before.mkdir();after.mkdir()

                    # 실행
                    old=subject['materialize'](original,before)
                    new=subject['materialize'](updated,after)
                    old_migration=subject['materialize'](original,before,True)
                    new_migration=subject['materialize'](updated,after,True)

                    # 검증: worker flag는 runtime만 받으며 credential, 기존 env, migration은 그대로다.
                    actual=(new/'runtime.env').read_text()
                    normalized=actual
                    for name,value in zip(ENV,(enabled,deletion)):
                        record=name+'='+value+'\n'
                        self.assertEqual(actual.count(record),1)
                        normalized=normalized.replace(record,name+'=false\n')
                    self.assertEqual(normalized,(old/'runtime.env').read_text())
                    self.assertEqual((old_migration/'runtime.env').read_bytes(),(new_migration/'runtime.env').read_bytes())
                    self.assertNotIn('Chat__Safety__',(new_migration/'runtime.env').read_text())
                    for file in old.iterdir():
                        if file.name!='runtime.env':self.assertEqual(file.read_bytes(),(new/file.name).read_bytes())

    def test_materialize_rejects_invalid_before_any_release_file_write(self):
        # 준비 / 실행 / 검증: 직접 deploy JSON도 runtime 공개 override와 같은 strict 검사를 받는다.
        subject=source_namespace()
        for value in (None,{'enabled':'false'},{'account_deletion_enabled':1},{'unknown':PRIVATE}):
            config=fixture_config('chat');config['safety']=value
            with patch.object(Path,'mkdir') as mkdir,patch.object(Path,'write_text') as write:
                with self.assertRaises(subject['DeployError']):subject['materialize'](config,Path('not-created'))
                mkdir.assert_not_called();write.assert_not_called()

    def test_existing_runtime_directory_still_fails_instead_of_overwriting_flags(self):
        # 준비 / 실행 / 검증: 이번 공개 flag 배선은 기존 release 재작성 보호를 제거하지 않는다.
        subject=source_namespace()
        subject['Path']=lambda path:SimpleNamespace(read_text=lambda:'SYNTHETIC_PUBLIC_ROOTS')
        subject['os']=SimpleNamespace(geteuid=lambda:1000,chmod=os.chmod)
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);config=fixture_config('chat')
            actual=subject['materialize'](config,root)
            before=(actual/'runtime.env').read_bytes()
            with self.assertRaises(FileExistsError):subject['materialize'](ship.apply_safety(config,'true','true'),root)
            self.assertEqual(before,(actual/'runtime.env').read_bytes())

    def test_main_public_validation_finishes_before_any_key_write_or_transport(self):
        # 준비 / 실행 / 검증
        for safety,deletion in ((PRIVATE,''),('true','FALSE'),('','false\nINJECT=1')):
            env={'GITHUB_SHA':'a'*40,'GITHUB_RUN_ID':'1','GITHUB_RUN_ATTEMPT':'1','OCI_HOST':'192.0.2.30',
                 'RUNTIME_CONFIG_JSON':json.dumps({'service':'chat','public_url':'https://192.0.2.30:8443'}),
                 'CHAT_SAFETY_ENABLED':safety,'CHAT_ACCOUNT_DELETION_ENABLED':deletion}
            argv=['ship.py','--service','chat','--image','ghcr.io/fixture/chat@sha256:'+'b'*64]
            with patch.object(ship.os,'environ',env),patch.object(ship.sys,'argv',argv),\
                 patch.object(ship.tempfile,'TemporaryDirectory') as directory,patch.object(ship.os,'chmod'),\
                 patch.object(Path,'write_text') as write,patch.object(ship,'checked') as transport:
                directory.return_value.__enter__.return_value='synthetic-directory-not-created'
                with self.assertRaises(RuntimeError) as error:ship.main()
                self.assertNotIn(PRIVATE,str(error.exception));write.assert_not_called();transport.assert_not_called()

    def test_actual_main_serializes_public_flags_before_keys_and_preserves_private_config(self):
        # 준비: 첫 runtime.json 쓰기에서 멈춰 실제 main의 입력 소비와 JSON 전달까지 검증한다.
        class Captured(RuntimeError):pass
        for enabled,deletion in (('true','true'),('false','false'),('true','false'),('false','true'),('','')):
            original=fixture_config('chat');original['public_url']='https://192.0.2.30:8443'
            original['nested']={'private':PRIVATE}
            env={'GITHUB_SHA':'a'*40,'GITHUB_RUN_ID':'1','GITHUB_RUN_ATTEMPT':'1','OCI_HOST':'192.0.2.30','GITHUB_ACTOR':'synthetic-owner',
                 'RUNTIME_CONFIG_JSON':json.dumps(original),'CHAT_SAFETY_ENABLED':enabled,'CHAT_ACCOUNT_DELETION_ENABLED':deletion}
            argv=['ship.py','--service','chat','--image','ghcr.io/fixture/chat@sha256:'+'b'*64]
            with patch.object(ship.os,'environ',env),patch.object(ship.sys,'argv',argv),\
                 patch.object(ship.tempfile,'TemporaryDirectory') as directory,patch.object(ship.os,'chmod'),\
                 patch.object(Path,'write_text',side_effect=Captured) as write,patch.object(ship,'checked') as transport:
                directory.return_value.__enter__.return_value='synthetic-directory-not-created'
                with self.assertRaises(Captured):ship.main()
                captured=json.loads(write.call_args.args[0])
                expected=dict(zip(FIELDS,(enabled=='true',deletion=='true')))
                self.assertEqual(captured,{**original,'safety':expected,'registry_user':'synthetic-owner'})
                transport.assert_not_called()

    def test_workflow_uses_step_environment_public_variables_and_main_reads_only_CHAT_branch(self):
        # 준비
        workflow=(HERE.parents[1]/'.github/workflows/deploy.yaml').read_text(encoding='utf-8')
        tree=ast.parse((HERE/'ship.py').read_text(encoding='utf-8'))
        main=next(node for node in tree.body if isinstance(node,ast.FunctionDef) and node.name=='main')
        branch=next(node for node in ast.walk(main) if isinstance(node,ast.If)
                    and ast.dump(node.test)==ast.dump(ast.parse("a.service=='chat'",mode='eval').body))

        # 검증: Actions 값을 shell 명령에 끼워 넣지 않고 고정 helper가 소비한다.
        for name in ('CHAT_SAFETY_ENABLED','CHAT_ACCOUNT_DELETION_ENABLED'):
            self.assertEqual(workflow.count('${{ vars.'+name+' }}'),1)
            self.assertIn('          '+name+': ${{ vars.'+name+' }}',workflow)
            self.assertNotIn('${{ secrets.'+name+' }}',workflow)
            self.assertEqual(sum(isinstance(node,ast.Constant) and node.value==name for node in ast.walk(branch)),1)
        self.assertEqual(sum(isinstance(node,ast.Call) and isinstance(node.func,ast.Name) and node.func.id=='apply_safety'
                             for node in ast.walk(branch)),1)
        self.assertIn('run: python3 deploy/oci/ship.py --service chat --image "$RELEASE_IMAGE"',workflow)

    def test_deployer_validates_safety_and_migration_still_completes_before_application_start(self):
        # 준비 / 실행
        source=(HERE/'deploy.py').read_text(encoding='utf-8');tree=ast.parse(source)
        validate=next(node for node in tree.body if isinstance(node,ast.FunctionDef) and node.name=='validate')
        program=(HERE.parents[1]/'TranslaCat.Chat.Api/Program.cs').read_text(encoding='utf-8')

        # 검증: 공개 enable은 schema 검증이나 DB 전용 migration 실행 순서를 건너뛰지 않는다.
        self.assertTrue(any(isinstance(node,ast.Call) and isinstance(node.func,ast.Name) and node.func.id=='chat_safety'
                            for node in ast.walk(validate)))
        self.assertLess(source.index("need(code=='0','MIGRATION_FAILED"),source.index("run(['docker','start',current])"))
        self.assertLess(program.index('--migrate-database'),program.index('builder.Services.AddChatRuntime'))


if __name__=='__main__':unittest.main()
