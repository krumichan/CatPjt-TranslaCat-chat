"""Portable actual-function tests; no curl, Docker, TLS connection or deployment is started."""
import ast
from contextlib import redirect_stdout
import io
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
from types import SimpleNamespace
import unittest

from test_native_origin_config import HERE, source_namespace


def subject_namespace():
    # 준비: Linux import 없이 제품의 실제 함수 본문을 컴파일한다.
    values=source_namespace()
    tree=ast.parse((HERE/'deploy.py').read_text(encoding='utf-8'))
    selected=[node for node in tree.body if isinstance(node,ast.FunctionDef)
              and node.name in {'verify_chat_https_ready','deploy','base_args'}]
    if len(selected)!=3:
        raise AssertionError('required deployment functions missing')
    exec(compile(ast.Module(body=selected,type_ignores=[]),str(HERE/'deploy.py'),'exec'),values)
    return values


class Clock:
    def __init__(self):
        self.now=0
        self.sleeps=[]

    def monotonic(self):
        return self.now

    def sleep(self,seconds):
        self.sleeps.append(seconds)
        self.now+=seconds


class ChatHttpsReadinessTests(unittest.TestCase):
    def setUp(self):
        self.subject=subject_namespace()
        self.clock=Clock()
        self.calls=[]
        self.subject['time']=self.clock
        self.subject['subprocess']=SimpleNamespace(
            PIPE=subprocess.PIPE,TimeoutExpired=subprocess.TimeoutExpired,run=self.invoke)
        self.responses=[]
        self.config={'service':'chat','public_url':'https://chat.fixture.invalid:8443'}

    def invoke(self,args,**kwargs):
        self.calls.append((args,kwargs))
        response=self.responses.pop(0)
        if isinstance(response,Exception):
            raise response
        code,status,body,duration=response
        self.clock.now+=duration
        return SimpleNamespace(returncode=code,stdout=body+'\n'+status,stderr='PRIVATE_NATIVE_OUTPUT')

    def ready(self):
        return (0,'200','{"status":"READY"}',0)

    def check(self):
        self.subject['verify_chat_https_ready'](self.config,Path('/synthetic/runtime'))

    def test_first_ready_preserves_strict_tls_host_and_request_arguments(self):
        # 실행 / 검증: 기존 TLS pin/resolve 경로와 shell 없는 인자를 유지한다.
        self.responses=[self.ready()]
        self.check()
        args,options=self.calls[0]
        for flag in ('--fail','--silent','--show-error','--cacert','--resolve'):
            self.assertIn(flag,args)
        self.assertEqual(str(Path('/synthetic/runtime')/'service-ca.pem'),args[args.index('--cacert')+1])
        self.assertEqual('chat.fixture.invalid:8443:127.0.0.1',args[args.index('--resolve')+1])
        self.assertEqual('https://chat.fixture.invalid:8443/api/ready',args[-1])
        self.assertNotIn('--insecure',args)
        self.assertNotIn('--location',args)
        self.assertNotIn('shell',options)
        self.assertEqual(15,options['timeout'])
        self.assertEqual([],self.clock.sleeps)

    def test_503_then_actual_ready_succeeds(self):
        self.responses=[(22,'503','',0),self.ready()]
        self.check()
        self.assertEqual(2,len(self.calls))
        self.assertEqual([3],self.clock.sleeps)

    def test_503_retries_stop_at_monotonic_deadline(self):
        self.responses=[(22,'503','',0)]*20
        with self.assertRaisesRegex(self.subject['DeployError'],'^CHAT_HTTPS_READINESS_503_TIMEOUT$'):
            self.check()
        self.assertEqual(30,self.clock.now)
        self.assertEqual(10,len(self.calls))
        self.assertEqual(10,len(self.clock.sleeps))

    def test_request_and_sleep_budgets_shrink_to_remaining_time(self):
        self.responses=[(22,'503','',14),(22,'503','',12.5),(22,'503','',0.5)]
        with self.assertRaisesRegex(self.subject['DeployError'],'503_TIMEOUT'):
            self.check()
        self.assertEqual(30,self.clock.now)
        self.assertEqual([3,0.5],self.clock.sleeps)
        self.assertEqual([15,13],[options['timeout'] for _,options in self.calls])
        for args,options in self.calls:
            self.assertEqual(options['timeout'],float(args[args.index('--max-time')+1]))
            self.assertLessEqual(float(args[args.index('--connect-timeout')+1]),options['timeout'])

    def test_other_http_statuses_fail_without_retry(self):
        for status in ('301','400','401','403','404','429','500','502','504'):
            with self.subTest(status=status):
                self.responses=[(22,status,'PRIVATE_BODY',0)]
                before=len(self.calls)
                with self.assertRaisesRegex(self.subject['DeployError'],'^CHAT_HTTPS_READINESS_REQUEST_FAILED$'):
                    self.check()
                self.assertEqual(before+1,len(self.calls))
        self.assertEqual([],self.clock.sleeps)

    def test_transport_error_with_503_text_still_fails_without_retry(self):
        for code in (1,6,7,28,35,60):
            with self.subTest(code=code):
                self.responses=[(code,'503','PRIVATE_BODY',0)]
                with self.assertRaisesRegex(self.subject['DeployError'],'^CHAT_HTTPS_READINESS_REQUEST_FAILED$'):
                    self.check()
        self.assertEqual([],self.clock.sleeps)

    def test_process_timeout_os_and_decode_errors_are_not_retried(self):
        for error in (subprocess.TimeoutExpired('PRIVATE_COMMAND',15),OSError('PRIVATE_PATH'),
                      UnicodeError('PRIVATE_BODY')):
            with self.subTest(error=type(error).__name__):
                self.responses=[error]
                with self.assertRaisesRegex(self.subject['DeployError'],'^CHAT_HTTPS_READINESS_TRANSPORT_FAILED$'):
                    self.check()
        self.assertEqual([],self.clock.sleeps)

    def test_200_without_actual_ready_is_rejected(self):
        for body in ('{"status":"NOT_READY"}','{"status":"ready"}','{}','[]','null','"READY"'):
            with self.subTest(body=body):
                self.responses=[(0,'200',body,0)]
                with self.assertRaisesRegex(self.subject['DeployError'],'^CHAT_HTTPS_READINESS_NOT_READY$'):
                    self.check()
        self.assertEqual([],self.clock.sleeps)

    def test_malformed_json_is_rejected_without_body_disclosure(self):
        self.responses=[(0,'200','PRIVATE_INVALID_JSON',0)]
        with self.assertRaisesRegex(self.subject['DeployError'],'^CHAT_HTTPS_READINESS_INVALID_BODY$'):
            self.check()
        self.assertEqual([],self.clock.sleeps)

    def test_malformed_status_is_not_retried(self):
        for status in ('000','503x','503\n',''):
            with self.subTest(status=status):
                self.responses=[(22,status,'PRIVATE_BODY',0)]
                with self.assertRaises(self.subject['DeployError']):
                    self.check()
        self.assertEqual([],self.clock.sleeps)

    def test_elapsed_response_cannot_succeed_after_deadline(self):
        self.responses=[(0,'200','{"status":"READY"}',31)]
        with self.assertRaisesRegex(self.subject['DeployError'],'^CHAT_HTTPS_READINESS_503_TIMEOUT$'):
            self.check()

    def test_invalid_config_is_rejected_before_transport(self):
        for config in ({'service':'ll','public_url':'https://chat.fixture.invalid:8443'},
                       {'service':'chat','public_url':'http://chat.fixture.invalid:8443'},
                       {'service':'chat','public_url':'https://chat.fixture.invalid'},
                       {'service':'chat','public_url':'https://chat.fixture.invalid:8443/api/ready'}):
            with self.subTest(config=config):
                self.config=config
                with self.assertRaises(self.subject['DeployError']):
                    self.check()
        self.assertEqual([],self.calls)

    def test_missing_curl_status_is_rejected(self):
        self.subject['subprocess'].run=lambda *args,**kwargs:SimpleNamespace(
            returncode=0,stdout='PRIVATE_BODY',stderr='PRIVATE_ERROR')
        with self.assertRaisesRegex(self.subject['DeployError'],'^CHAT_HTTPS_READINESS_INVALID_RESPONSE$'):
            self.check()


class DeploymentReadinessRoutingTests(unittest.TestCase):
    def test_actual_deploy_orders_migration_local_ready_then_chat_tls_only(self):
        # 준비: 실제 deploy 함수를 합성 파일과 외부 호출 대역으로 실행한다.
        for service in ('chat','ll'):
            with self.subTest(service=service),tempfile.TemporaryDirectory() as directory:
                values=subject_namespace()
                calls=[]
                sha='a'*40
                def command(args,**kwargs):
                    calls.append(list(args))
                    return '0' if args[:2]==['docker','wait'] else ''
                def materialize(config,release,migration=False):
                    (release/('migration' if migration else 'runtime')).mkdir()
                values.update(ROOT=Path(directory),OWNER='synthetic-owner',
                    os=SimpleNamespace(geteuid=lambda:0,chmod=lambda *args:None,environ={}),
                    fcntl=SimpleNamespace(LOCK_EX=1,LOCK_NB=2,flock=lambda *args:None),
                    shutil=shutil,run=command,materialize=materialize,
                    inspect=lambda kind,name:({'Config':{'Labels':{'org.opencontainers.image.revision':sha}}}
                        if kind=='image' else None),
                    assert_vm_role=lambda *args:None,external_preflight=lambda *args:None,
                    network=lambda *args:None,volume=lambda *args:None,ensure_redis=lambda *args:None,
                    wait_ready=lambda *args: calls.append(['local-ready']) or True,
                    verify_chat_https_ready=lambda *args:calls.append(['chat-tls-ready']))
                config={'service':service,'registry_user':'synthetic',
                    'public_url':'https://fixture.invalid:8443','redis':{'network':'synthetic'}}
                token=Path(directory)/'synthetic-token'
                token.write_text('SYNTHETIC_FIXTURE',encoding='utf-8')

                # 실행 / 검증: 마이그레이션 성공과 로컬 준비 후에만 최종 TLS 검사를 호출한다.
                with redirect_stdout(io.StringIO()):
                    values['deploy'](config,'ghcr.io/fixture/app@sha256:'+'b'*64,
                        sha,sha+'-1-1',token)
                migration=next(index for index,call in enumerate(calls) if call[:2]==['docker','wait'])
                local=calls.index(['local-ready'])
                self.assertLess(migration,local)
                if service=='chat':
                    self.assertGreater(calls.index(['chat-tls-ready']),local)
                    self.assertFalse(any(call[0]=='curl' for call in calls))
                else:
                    self.assertNotIn(['chat-tls-ready'],calls)
                    curl=[call for call in calls if call[0]=='curl']
                    self.assertEqual(1,len(curl))
                    self.assertEqual('https://fixture.invalid:8443/health/ready',curl[0][-1])
                    self.assertEqual('15',curl[0][curl[0].index('--max-time')+1])
                result=json.loads(next((Path(directory)/service/'releases').glob('*/result.json')).read_text())
                self.assertEqual('DEPLOYED_READY',result['status'])
                self.assertEqual('NOT_RUN',result['business_e2e'])


if __name__=='__main__':
    unittest.main()
