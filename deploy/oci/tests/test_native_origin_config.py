"""Portable source-function tests; Linux deployment/flock/permissions are not exercised here."""
import ast
import base64
import copy
import hashlib
import ipaddress
import json
import os
from pathlib import Path
import re
from types import SimpleNamespace
import tempfile
import unittest
from urllib.parse import urlparse


HERE=Path(__file__).resolve().parents[1]
SELECTED={'DeployError','need','one_line','origin','is_canonical_native_origin','native_origin',
          'env_text','connection','write','materialize','mobile_push','chat_safety','parse_runtime_config'}


def source_namespace():
    # Linux 전용 fcntl import 없이 실제 함수 본문을 그대로 실행한다. 배포·locking 검증 성공으로 취급하지 않는다.
    tree=ast.parse((HERE/'deploy.py').read_text(encoding='utf-8'))
    selected=[node for node in tree.body if isinstance(node,(ast.FunctionDef,ast.ClassDef)) and node.name in SELECTED]
    if {node.name for node in selected}!=SELECTED:
        raise AssertionError('required source function missing')
    values={'__name__':'portable_deployment_functions','Path':Path,'re':re,'ipaddress':ipaddress,
            'urlparse':urlparse,'os':os,'json':json}
    exec(compile(ast.Module(body=selected,type_ignores=[]),str(HERE/'deploy.py'),'exec'),values)
    return values


def fixture_config(service='chat'):
    # 기존 Linux fixture의 합성 설정 factory를 재사용한다. 운영 JSON은 읽지 않는다.
    tree=ast.parse((HERE/'tests/test_deployment.py').read_text(encoding='utf-8'))
    factory=next(node for node in tree.body if isinstance(node,ast.FunctionDef) and node.name=='config')
    namespace={'base64':base64,'hashlib':hashlib,
               'PEM':'-----BEGIN CERTIFICATE-----\nSYNTHETIC_FIXTURE\n-----END CERTIFICATE-----\n'}
    exec(compile(ast.Module(body=[factory],type_ignores=[]),'<synthetic-config>','exec'),namespace)
    return namespace['config'](service)


class NativeOriginTests(unittest.TestCase):
    def setUp(self):
        self.subject=source_namespace()

    def test_canonical_https_origins(self):
        # 실행 / 검증: 단일 DNS/IP origin만 통과한다. 실제 DNS/TLS 요청은 없다.
        for value in ['https://native.fixture.invalid','https://198.51.100.20',
                      'https://native.fixture.invalid:8443','https://[2001:db8::20]']:
            with self.subTest(case=value):
                self.assertTrue(self.subject['is_canonical_native_origin'](value))

    def test_invalid_values_and_noncanonical_origins(self):
        # 준비
        values=[None,False,1,[],{},'', ' ', '*','null','http://native.fixture.invalid',
                'HTTPS://native.fixture.invalid','https://NATIVE.fixture.invalid','https://native.fixture.invalid.',
                'https://native.fixture.invalid/','https://native.fixture.invalid/path','https://native.fixture.invalid?',
                'https://native.fixture.invalid#','https://user@native.fixture.invalid','https://:password@native.fixture.invalid',
                'https://*.fixture.invalid','https://native.fixture.invalid:443','https://native.fixture.invalid:08443',
                'https://native.fixture.invalid:0','https://native.fixture.invalid:65536','https://native.fixture.invalid:abc',
                'https://198.051.100.20','https://0x7f000001','https://[2001:0db8::20]',
                'https://native.fixture.invalid,https://other.fixture.invalid','https://native.fixture.invalid\nINJECT=1',
                'https://native.fixture.invalid\t','https://native.fixture.invalid\x00','https://한글.fixture.invalid']

        # 실행 / 검증
        for index,value in enumerate(values):
            with self.subTest(case=index):
                self.assertFalse(self.subject['is_canonical_native_origin'](value))

    def test_optional_value_and_browser_duplicate(self):
        # 준비 / 실행 / 검증: 미설정과 명시 빈 값은 다르게 처리하고 index0와 중복하지 않는다.
        self.assertIsNone(self.subject['native_origin']({'browser_origin':'https://web.fixture.invalid'}))
        for value in ['',None,'https://web.fixture.invalid']:
            with self.assertRaises(self.subject['DeployError']):
                self.subject['native_origin']({'browser_origin':'https://web.fixture.invalid','native_origin':value})

    def test_materialize_changes_only_the_selected_runtime_record(self):
        # 준비: Linux의 공개 CA 읽기와 ownership만 fixture로 대체하고 실제 serializer 본문·파일 쓰기를 사용한다.
        subject=self.subject
        subject['Path']=lambda path: SimpleNamespace(read_text=lambda:'SYNTHETIC_PUBLIC_ROOTS')
        subject['os']=SimpleNamespace(geteuid=lambda:1000,chmod=os.chmod)
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory)
            for service in ('chat','ll'):
                before_config=fixture_config(service); after_config=copy.deepcopy(before_config)
                after_config['native_origin']='https://198.51.100.20'
                (root/service/'before').mkdir(parents=True)
                (root/service/'after').mkdir(parents=True)
                before=subject['materialize'](before_config,root/service/'before')
                after=subject['materialize'](after_config,root/service/'after')
                before_migration=subject['materialize'](before_config,root/service/'before',True)
                after_migration=subject['materialize'](after_config,root/service/'after',True)

                # 검증: optional 한 행 외에 credential/CA/env 원문과 migration/LL 경로가 같다.
                added=b'Chat__Realtime__AllowedOrigins__1=https://198.51.100.20\n'
                actual=(after/'runtime.env').read_bytes()
                self.assertEqual(actual.count(added),1 if service=='chat' else 0)
                self.assertEqual(actual.replace(added,b''),(before/'runtime.env').read_bytes())
                self.assertEqual((before_migration/'runtime.env').read_bytes(),(after_migration/'runtime.env').read_bytes())
                for file in before.iterdir():
                    if file.name!='runtime.env':
                        self.assertEqual(file.read_bytes(),(after/file.name).read_bytes())


if __name__=='__main__':
    unittest.main()
