"""Offline contract tests: never contact OCI/MySQL/Redis/GitHub or a real Docker daemon."""
import base64, copy, hashlib, importlib.util, json, os, subprocess, tempfile, unittest
from pathlib import Path
from unittest.mock import patch

HERE = Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('deployment',HERE/'deploy.py')
d=importlib.util.module_from_spec(spec);spec.loader.exec_module(d)
PEM='-----BEGIN CERTIFICATE-----\nOFFLINE_FIXTURE_NOT_A_TRUST_ANCHOR\n-----END CERTIFICATE-----\n'
SHA='a'*40;IMAGE='ghcr.io/owner/project@sha256:'+'b'*64;RID=SHA+'-123-1'

def config(service='ll'):
    c=dict(schema_version=1,service=service,expected_vm_private_ip='10.0.1.10' if service=='ll' else '10.0.1.11',public_url='https://150.230.61.24:8443',timezone='UTC',
        database=dict(host='10.0.2.10',port=3306,name='translacat_'+service,username='translacat_'+service+'_app',
            password='Runtime_Only!$literal',migration_username='translacat_'+service+'_migrator',migration_password='Migration_Only!$literal'),
        ai_url='https://161.33.34.50:8443',ai_api_key='independent-opaque-key',mysql_ca_pem=PEM,service_ca_pem=PEM,
        expected_mysql_ca_sha256=hashlib.sha256(PEM.encode()).hexdigest(),allow_app_rollback_after_migration=False,registry_user='owner')
    if service=='ll': c.update(internal_jwt_key_base64=base64.b64encode(b'a'*32).decode(),tts_model='approved-model',audio_base_url=c['public_url'])
    else: c.update(be_url='https://217.142.230.186:8443',browser_origin='https://frontend.example',
        user_jwt_key_base64=base64.b64encode(b'a'*32).decode(),ingress_key_base64=base64.b64encode(b'b'*32).decode(),
        identity_key_base64=base64.b64encode(b'c'*32).decode(),
        redis=dict(container_name='translacat-chat-redis',network='translacat-chat-private',user='chat',namespace='translacat:chat:Production',password='d'*64))
    return c

class ValidationTests(unittest.TestCase):
    def rejected(self,change,service='ll'):
        c=config(service);change(c)
        with self.assertRaises((d.DeployError,ValueError)): d.validate(c,service)
    def test_valid_ll(self): self.assertEqual(d.validate(config(),'ll')['service'],'ll')
    def test_valid_chat(self): self.assertEqual(d.validate(config('chat'),'chat')['service'],'chat')
    def test_wrong_database(self): self.rejected(lambda c:c['database'].update(name='mysql'))
    def test_wrong_runtime_user(self): self.rejected(lambda c:c['database'].update(username='dbadmin'))
    def test_wrong_migration_user(self): self.rejected(lambda c:c['database'].update(migration_username='dbadmin'))
    def test_same_password_allowed_for_distinct_accounts(self):
        c=config('chat');c['database']['migration_password']=c['database']['password']
        validated=d.validate(c,'chat')
        self.assertNotEqual(validated['database']['username'],validated['database']['migration_username'])
    def test_placeholder_blocked(self): self.rejected(lambda c:c.update(ai_api_key='REPLACE_KEY'))
    def test_newline_blocked(self): self.rejected(lambda c:c['database'].update(password='valid\nINJECT=value'))
    def test_http_outbound_blocked(self): self.rejected(lambda c:c.update(ai_url='http://161.33.34.50:8000'))
    def test_public_port_fixed(self): self.rejected(lambda c:c.update(public_url='https://150.230.61.24:443'))
    def test_credentials_in_url_blocked(self): self.rejected(lambda c:c.update(ai_url='https://user:secret@161.33.34.50:8443'))
    def test_ca_hash_not_auto_trusted(self): self.rejected(lambda c:c.update(expected_mysql_ca_sha256='0'*64))
    def test_no_private_key_in_ca(self): self.rejected(lambda c:c.update(service_ca_pem=PEM+'PRIVATE KEY'))
    def test_ll_redis_rejected(self): self.rejected(lambda c:c.update(redis={}))
    def test_chat_key_directions(self): self.rejected(lambda c:c.update(identity_key_base64=c['ingress_key_base64']),'chat')
    def test_short_jwt_blocked(self): self.rejected(lambda c:c.update(internal_jwt_key_base64=base64.b64encode(b'abc').decode()))
    def test_redis_password_shape(self): self.rejected(lambda c:c['redis'].update(password='weak'),'chat')
    def test_timezone_no_absolute_path(self): self.rejected(lambda c:c.update(timezone='/etc/passwd'))
    def test_no_tls_skip_arguments(self):
        for service in ('ll','chat'):
            c=config(service);args=d.base_args(c,Path('/tmp/config'),'test',IMAGE)
            self.assertNotIn('--privileged',args);self.assertIn('--read-only',args)
            self.assertIn('127.0.0.1:8081:8081' if service=='ll' else '127.0.0.1:5085:8080',args)
    def test_connection_password_quoted_as_data(self):
        c=config('chat');c['database']['password']='Semi;colon"$notexecuted'
        s=d.connection(c)
        self.assertIn('Password="Semi;colon""$notexecuted"',s);self.assertIn('SslMode="VerifyCA"',s)
    def test_environment_not_shell_code(self):
        self.assertEqual(d.env_text({'VALUE':'$(touch /tmp/should-not-exist)\"x'}),'VALUE=$(touch /tmp/should-not-exist)\"x\n')
    def test_materialize_ll_credentials_separated(self):
        with tempfile.TemporaryDirectory() as td,patch.object(d.os,'geteuid',return_value=1000):
            c=config();r=d.materialize(c,Path(td));m=d.materialize(c,Path(td),True)
            runtime=(r/'runtime.env').read_text();migration=(m/'runtime.env').read_text()
            self.assertIn('DB_MIGRATION_MODE=validate',runtime);self.assertNotIn(c['database']['migration_password'],runtime)
            self.assertIn('DB_MIGRATION_MODE=migrate',migration);self.assertNotIn(c['ai_api_key'],migration)
            self.assertEqual((r/'runtime.env').stat().st_mode & 0o777,0o600)
            self.assertEqual(r.stat().st_mode & 0o777,0o700)
    def test_materialize_chat_credentials_separated(self):
        with tempfile.TemporaryDirectory() as td,patch.object(d.os,'geteuid',return_value=1000):
            c=config('chat');r=d.materialize(c,Path(td));m=d.materialize(c,Path(td),True)
            self.assertIn(c['database']['password'],(r/'db_connection').read_text())
            self.assertNotIn(c['database']['migration_password'],(r/'db_connection').read_text())
            self.assertFalse((m/'user_jwt').exists());self.assertFalse((m/'redis_password').exists())
            self.assertIn('SSL_CERT_FILE=/run/config/ca-bundle.pem',(r/'runtime.env').read_text())

class RedisTests(unittest.TestCase):
    def existing(self,running=True):
        return {'Config':{'Image':'redis:7.4.10-alpine','Labels':{}},'HostConfig':{'NetworkMode':'private','PortBindings':{}},
            'NetworkSettings':{'Networks':{'translacat-chat-private':{}}},'State':{'Running':running}}
    def test_running_preserved(self):
        with patch.object(d,'inspect',return_value=self.existing()),patch.object(d,'network'),patch.object(d,'run',return_value='PONG') as run:
            d.ensure_redis(config('chat'),Path('/not-used'))
            self.assertEqual(len(run.call_args_list),1)
            self.assertEqual(run.call_args.args[0][:3],['docker','exec','-i'])
            self.assertNotIn('d'*64,' '.join(run.call_args.args[0]))
    def test_stopped_started_not_recreated(self):
        with patch.object(d,'inspect',return_value=self.existing(False)),patch.object(d,'network'),patch.object(d,'run',return_value='PONG') as run:
            d.ensure_redis(config('chat'),Path('/not-used'))
            cmds=[x.args[0] for x in run.call_args_list]
            self.assertEqual(cmds[0],['docker','start','translacat-chat-redis']);self.assertFalse(any('rm' in x for x in cmds))
    def test_exposed_redis_rejected(self):
        r=self.existing();r['HostConfig']['PortBindings']={'6379/tcp':[{}]}
        with patch.object(d,'inspect',return_value=r), self.assertRaises(d.DeployError): d.ensure_redis(config('chat'),Path('/not-used'))
    def test_wrong_password_not_reset(self):
        with patch.object(d,'inspect',return_value=self.existing()),patch.object(d,'network'),patch.object(d.time,'sleep'),patch.object(d,'run',return_value='NOAUTH') as run:
            with self.assertRaises(d.DeployError): d.ensure_redis(config('chat'),Path('/not-used'))
            self.assertTrue(all(x.args[0][1]=='exec' for x in run.call_args_list))
    def test_old_compose_name_blocks_second_cache(self):
        with patch.object(d,'inspect',return_value=None),patch.object(d,'run',return_value='old-chat-redis') as run:
            with self.assertRaises(d.DeployError): d.ensure_redis(config('chat'),Path('/not-used'))
            self.assertEqual(run.call_count,1)
    def test_new_redis_private_persistent_no_argv_password(self):
        with tempfile.TemporaryDirectory() as td,patch.object(d,'inspect',return_value=None),patch.object(d,'network'),patch.object(d,'volume'),patch.object(d.os,'chown'),patch.object(d,'run') as run:
            run.side_effect=lambda args,**kw:'PONG' if args[1]=='exec' else ''
            c=config('chat');d.ensure_redis(c,Path(td));calls=[x.args[0] for x in run.call_args_list]
            create=next(x for x in calls if x[1]=='run')
            self.assertNotIn('-p',create);self.assertNotIn('--publish',create)
            self.assertIn('type=volume,src=translacat-chat-redis-data,dst=/data',create)
            self.assertNotIn(c['redis']['password'],(Path(td)/'redis/users.acl').read_text())
            self.assertIn('appendonly yes',(Path(td)/'redis/redis.conf').read_text())

class FakeDocker:
    def __init__(self,migration_exit='0'):
        self.commands=[];self.migration_exit=migration_exit
        self.objects={'translacat-ll':{'Id':'previous','Config':{'Labels':{'tc.owner':d.OWNER}},'State':{'Running':True}}}
    def inspect(self,kind,name):
        if kind=='image': return {'Config':{'Labels':{'org.opencontainers.image.revision':SHA}}}
        return copy.deepcopy(self.objects.get(name))
    def run(self,args,**kwargs):
        args=[str(x) for x in args];self.commands.append(args)
        if args[0]=='curl': return '{"status":"READY"}'
        op=args[1]
        if op=='create':
            n=args[args.index('--name')+1];self.objects[n]={'Id':'new-'+n,'Config':{'Labels':{'tc.owner':d.OWNER}},'State':{'Running':False}}
        elif op=='start': self.objects[args[2]]['State']['Running']=True
        elif op=='stop': self.objects[args[-1]]['State']['Running']=False
        elif op=='rename': self.objects[args[3]]=self.objects.pop(args[2])
        elif op=='rm': self.objects.pop(args[-1],None)
        elif op=='wait': return self.migration_exit
        return ''

class ReleaseTests(unittest.TestCase):
    def exercise(self,db_exit='0',ready=True,rollback=False):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td);token=root/'token';token.write_text('short-lived-fixture-token');backend=FakeDocker(db_exit);c=config();c['allow_app_rollback_after_migration']=rollback
            with patch.object(d,'ROOT',root),patch.object(d,'run',side_effect=backend.run),patch.object(d,'inspect',side_effect=backend.inspect),\
                 patch.object(d,'network'),patch.object(d,'volume'),patch.object(d,'external_preflight'),patch.object(d,'assert_vm_role'),patch.object(d,'wait_ready',return_value=ready),patch.object(d.os,'geteuid',return_value=0),patch.object(d.os,'chown'):
                error=None
                try: d.deploy(c,IMAGE,SHA,RID,token)
                except d.DeployError as e: error=str(e)
            self.assertFalse(token.exists());self.assertFalse((root/'ll/releases'/RID/'migration').exists())
            return backend,error
    def test_failed_migration_keeps_previous_running(self):
        b,e=self.exercise(db_exit='1');self.assertIn('MIGRATION_FAILED',e)
        self.assertTrue(b.objects['translacat-ll']['State']['Running']);self.assertFalse(any(x[1]=='stop' and x[-1]=='translacat-ll' for x in b.commands))
    def test_readiness_failure_without_schema_approval_preserves_old_stopped(self):
        b,e=self.exercise(ready=False)
        self.assertIn('APPLICATION_READINESS_FAILED',e)
        old=next(v for v in b.objects.values() if v['Id']=='previous');self.assertFalse(old['State']['Running'])
    def test_approved_backward_compatible_rollback(self):
        b,e=self.exercise(ready=False,rollback=True)
        self.assertEqual(b.objects['translacat-ll']['Id'],'previous');self.assertTrue(b.objects['translacat-ll']['State']['Running'])
    def test_success_keeps_previous_for_recovery(self):
        b,e=self.exercise();self.assertIsNone(e);self.assertTrue(b.objects['translacat-ll']['State']['Running'])
        self.assertTrue(any(v['Id']=='previous' for v in b.objects.values()))
    def test_migration_precedes_stopping_app(self):
        b,e=self.exercise();wait=next(i for i,x in enumerate(b.commands) if x[1]=='wait');stop=next(i for i,x in enumerate(b.commands) if x[1]=='stop')
        self.assertLess(wait,stop)
    def test_mutable_image_ref_rejected(self):
        with tempfile.TemporaryDirectory() as td,patch.object(d.os,'geteuid',return_value=0):
            with self.assertRaises(d.DeployError): d.deploy(config(),'image:latest',SHA,RID,Path(td)/'token')

class SourceContracts(unittest.TestCase):
    def test_entrypoint_no_eval(self):
        s=(HERE/'entrypoint.sh').read_text();self.assertNotIn('eval ',s);self.assertNotIn('source /run',s)
    def test_transport_strict_host_and_no_agent_forwarding(self):
        s=(HERE/'ship.py').read_text();self.assertIn('StrictHostKeyChecking yes',s);self.assertIn('ForwardAgent no',s)
        self.assertNotIn('StrictHostKeyChecking no',s)
    def test_no_unbounded_stop_or_prune_redis(self):
        s=(HERE/'deploy.py').read_text();self.assertNotIn('system prune',s);self.assertNotIn('volume prune',s);self.assertNotIn('flushall',s.lower().replace('no flushall',''))
    def test_shell_syntax(self): subprocess.run(['sh','-n',str(HERE/'entrypoint.sh')],check=True)

if __name__=='__main__': unittest.main()
