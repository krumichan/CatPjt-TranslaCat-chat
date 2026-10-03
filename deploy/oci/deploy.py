#!/usr/bin/env python3
"""OCI LL/CHAT 릴리스. root에서 실행; SQL 원문/비밀번호를 로그에 쓰지 않는다."""
from __future__ import annotations
import argparse, base64, fcntl, signal, hashlib, ipaddress, json, os, re, shutil, ssl, subprocess, sys, time
from pathlib import Path
from urllib.parse import urlparse
from urllib.request import urlopen, Request

ROOT = Path('/srv/translacat')
OWNER = 'translacat-oci-v1'
REDIS_IMAGE = 'redis:7.4.10-alpine@sha256:e7723ff73d963f5cc6d9c4643ea3d989527a402a319239054e9472a7fb9219a2'
class DeployError(Exception): pass

def need(ok, code):
    if not ok: raise DeployError(code)

def run(args, *, data=None, timeout=120, env=None):
    try:
        p = subprocess.run([str(a) for a in args], input=data, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, timeout=timeout, env=env, text=True)
    except (subprocess.TimeoutExpired, OSError):
        raise DeployError('COMMAND_TIMEOUT_OR_UNAVAILABLE:' + str(args[0])) from None
    if p.returncode:
        # argv, SQL, DB driver message, native output may contain credentials.
        raise DeployError('COMMAND_FAILED:' + str(args[0]) + ':exit=' + str(p.returncode))
    return p.stdout

def inspect(kind, name):
    p = subprocess.run(['docker', kind, 'inspect', name], capture_output=True, text=True)
    if p.returncode: return None
    return json.loads(p.stdout)[0]

def one_line(value, name):
    need(isinstance(value, str) and value and not any(ord(c)<32 or ord(c)==127 for c in value), 'INVALID:' + name)
    need('REPLACE_' not in value, 'MISSING:' + name)
    return value

def origin(value, name):
    value=one_line(value,name); u=urlparse(value)
    need(u.scheme=='https' and u.hostname and not u.username and not u.password and
         u.path in ('','/') and not u.query and not u.fragment, 'HTTPS_ORIGIN_REQUIRED:' + name)
    return value.rstrip('/')

def signing(value, name):
    one_line(value,name)
    try: raw=base64.b64decode(value,validate=True)
    except ValueError: raise DeployError('INVALID_BASE64:' + name) from None
    need(len(raw)>=32,'SHORT_SIGNING_KEY:' + name)
    return value

def validate(c, service):
    need(c.get('service')==service,'SERVICE_MISMATCH')
    need(c.get('schema_version')==1,'CONFIG_VERSION')
    ipaddress.IPv4Address(c['expected_vm_private_ip'])
    db=c['database']; need(db['name']=='translacat_'+service,'DATABASE_MISMATCH')
    ipaddress.IPv4Address(db['host']); need(db['port']==3306,'DATABASE_PORT')
    need(db['username']=='translacat_'+service+'_app','RUNTIME_ACCOUNT_MISMATCH')
    need(db['migration_username']=='translacat_'+service+'_migrator','MIGRATION_ACCOUNT_MISMATCH')
    for k in ('username','password','migration_username','migration_password'): one_line(db[k], 'database.'+k)
    # Runtime과 migration은 서로 다른 MySQL 계정/권한으로 분리한다.
    # 운영자가 관리 편의를 위해 동일한 암호를 선택하는 것은 허용한다.
    need(db['username'] != db['migration_username'],'SEPARATE_DATABASE_ACCOUNTS_REQUIRED')
    one_line(c['timezone'],'timezone')
    need(not c['timezone'].startswith('/'),'TIMEZONE_INVALID')
    origin(c['public_url'],'public_url')
    need(urlparse(c['public_url']).port==8443,'PUBLIC_HTTPS_PORT_MUST_BE_8443')
    need(Path('/usr/share/zoneinfo',c['timezone']).is_file() and '..' not in c['timezone'],'TIMEZONE_INVALID')
    origin(c['ai_url'],'ai_url'); one_line(c['ai_api_key'],'ai_api_key')
    need(c['ai_api_key'].isascii() and not any(c.isspace() for c in c['ai_api_key']),'AI_KEY_HEADER_FORMAT')
    need(isinstance(c['allow_app_rollback_after_migration'],bool),'ROLLBACK_POLICY_REQUIRED')
    for k in ('mysql_ca_pem','service_ca_pem'):
        need(isinstance(c[k],str) and c[k].count('-----BEGIN CERTIFICATE-----')==1 and
             'PRIVATE KEY' not in c[k], 'SINGLE_PUBLIC_CA_REQUIRED:'+k)
    # User-reviewed PEM hash. This is integrity pinning, not an independent source of trust.
    need(re.fullmatch('[A-Fa-f0-9]{64}',c['expected_mysql_ca_sha256']) is not None,'MYSQL_CA_HASH_REQUIRED')
    need(hashlib.sha256(c['mysql_ca_pem'].encode()).hexdigest().lower()==
         c['expected_mysql_ca_sha256'].lower(),'MYSQL_CA_HASH_MISMATCH')
    if service=='chat':
        origin(c['be_url'],'be_url'); origin(c['browser_origin'],'browser_origin')
        vals=[signing(c[k],k) for k in ('user_jwt_key_base64','ingress_key_base64','identity_key_base64')]
        need(len(set(vals))==3 and c['ai_api_key'] not in vals,'DIRECTION_KEYS_MUST_DIFFER')
        redis=c['redis']; one_line(redis['container_name'],'redis.container_name'); one_line(redis['network'],'redis.network')
        for k in ('container_name','network'): need(re.fullmatch('[a-zA-Z0-9][a-zA-Z0-9_.-]{0,100}',redis[k]) is not None,'REDIS_NAME')
        need(re.fullmatch('[0-9a-fA-F]{64}',redis['password']) is not None,'REDIS_PASSWORD_64_HEX_REQUIRED')
        need(redis['user']=='chat','REDIS_USER_MUST_BE_CHAT')
        need(re.fullmatch('translacat:chat:[A-Za-z0-9:_-]+',redis['namespace']) is not None,'REDIS_NAMESPACE')
    else:
        need('redis' not in c,'LL_DOES_NOT_USE_REDIS')
        signing(c['internal_jwt_key_base64'],'internal_jwt_key_base64')
        one_line(c['tts_model'],'tts_model'); origin(c['audio_base_url'],'audio_base_url')
    return c

def write(path, data, *, uid=10001):
    path.parent.mkdir(parents=True,exist_ok=True)
    with open(path,'x',encoding='utf-8',newline='\n') as f: f.write(data)
    os.chmod(path,0o600)
    if os.geteuid()==0: os.chown(path,uid,uid)

def env_text(env):
    for k,v in env.items():
        need(re.fullmatch('[A-Za-z_][A-Za-z0-9_]*',k) is not None,'ENV_NAME')
        one_line(str(v),k)
    return ''.join(f'{k}={v}\n' for k,v in env.items())

def connection(c, migrate=False):
    db=c['database']; prefix='migration_' if migrate else ''
    # MySql.Data connection string quoting; a semicolon in a password remains data.
    q=lambda v:'"'+str(v).replace('"','""')+'"'
    return ';'.join(f'{k}={q(v)}' for k,v in {
        'Server':db['host'],'Port':db['port'],'Database':db['name'],
        'User ID':db[prefix+'username'],'Password':db[prefix+'password'],
        'SslMode':'VerifyCA','SslCa':'/run/config/mysql-ca.pem',
        'MaximumPoolSize':4,'Connection Timeout':10}.items())+';'

def materialize(c, release, migrate=False):
    service=c['service']; dst=release/('migration' if migrate else 'runtime')
    dst.mkdir(mode=0o700)
    if os.geteuid()==0: os.chown(dst,10001,10001)
    for k in ('mysql_ca_pem','service_ca_pem'):
        write(dst/('mysql-ca.pem' if k=='mysql_ca_pem' else 'service-ca.pem'),c[k])
    # Uses trusted roots + the service root; never replaces all public roots with a DB leaf.
    roots=Path('/etc/ssl/certs/ca-certificates.crt').read_text()
    write(dst/'ca-bundle.pem',roots+'\n'+c['service_ca_pem'])
    env={'TZ':c['timezone']}
    if service=='ll':
        db=c['database']; prefix='migration_' if migrate else ''
        env.update(DB_JDBC_URL=f"jdbc:mysql://{db['host']}:3306/translacat_ll?sslMode=VERIFY_CA&trustCertificateKeyStoreUrl=file:/tmp/mysql-trust.p12&trustCertificateKeyStoreType=PKCS12&trustCertificateKeyStorePassword=changeit&fallbackToSystemTrustStore=false",
            DB_EXPECTED_CATALOG='translacat_ll', DB_USERNAME=db[prefix+'username'],DB_PASSWORD=db[prefix+'password'],
            DB_MIGRATION_MODE='migrate' if migrate else 'validate',DB_MAX_POOL_SIZE='4',DB_MIN_IDLE='0')
        if migrate: env['LL_ALLOW_MIGRATIONS']='true'
        else:
            env.update(LL_INTERNAL_JWT_SECRET_BASE64=c['internal_jwt_key_base64'],AI_SERVER_URL=c['ai_url'],AI_SERVER_API_KEY=c['ai_api_key'],
                LL_LEVEL_TEST_AUDIO_UPLOAD_BASE_URL=c['audio_base_url'],LL_LEVEL_TEST_AUDIO_ROOT='/app/data/level-test-audio',
                LL_SPEAKING_TTS_MODEL=c['tts_model'])
    else:
        write(dst/'db_connection',connection(c,migrate))
        if not migrate:
            for file,k in [('user_jwt','user_jwt_key_base64'),('ingress_jwt','ingress_key_base64'),('identity_jwt','identity_key_base64'),('ai_key','ai_api_key')]:
                write(dst/file,c[k])
            write(dst/'redis_password',c['redis']['password'])
            env.update({
                'AllowedHosts':'localhost;127.0.0.1;'+urlparse(c['public_url']).hostname,
                'Chat__SourceTimeZone':c['timezone'],
                'Chat__Database__ConnectionString_FILE':'/run/config/db_connection',
                'Chat__Authentication__Enabled':'true','Chat__Authentication__Base64SigningKey_FILE':'/run/config/user_jwt',
                'Chat__Redis__Endpoint':c['redis']['container_name']+':6379','Chat__Redis__User':'chat',
                'Chat__Redis__Password_FILE':'/run/config/redis_password','Chat__Redis__Namespace':c['redis']['namespace'],
                'Chat__Redis__UseTls':'false','Chat__Redis__AllowPrivatePlaintext':'true','Chat__Presence__Enabled':'true',
                'Chat__Realtime__AllowedOrigins__0':c['browser_origin'],
                'Chat__ServiceAuthentication__Ingress__Enabled':'true',
                'Chat__ServiceAuthentication__Ingress__Issuer':'translacat-be','Chat__ServiceAuthentication__Ingress__Audience':'translacat-chat',
                'Chat__ServiceAuthentication__Ingress__Service':'translacat-be','Chat__ServiceAuthentication__Ingress__Base64SigningKey_FILE':'/run/config/ingress_jwt',
                'SSL_CERT_FILE':'/run/config/ca-bundle.pem','DOTNET_GCHeapHardLimitPercent':'60','DOTNET_PROCESSOR_COUNT':'1'})
            for section in ('Identity','Core'):
                stem='Chat__'+section+'__';env.update({stem+'Enabled':'true',stem+'BaseUrl':c['be_url'],stem+'TimeoutSeconds':'5',
                    stem+'ServiceAuthentication__Issuer':'translacat-chat',stem+'ServiceAuthentication__Audience':'translacat-be',
                    stem+'ServiceAuthentication__Service':'translacat-chat',stem+'ServiceAuthentication__Base64SigningKey_FILE':'/run/config/identity_jwt'})
            for section in ('Ai','Translation'):
                stem='Chat__'+section+'__';env.update({stem+'Enabled':'true',stem+'AiBaseUri':c['ai_url'],stem+'ApiKey_FILE':'/run/config/ai_key'})
    write(dst/'runtime.env',env_text(env))
    return dst

def network(name, internal=False):
    old=inspect('network',name)
    if old:
        need(old['Driver']=='bridge' and old['Internal']==internal,'NETWORK_CONTRACT_MISMATCH:'+name)
    else: run(['docker','network','create','--label',f'tc.owner={OWNER}',*(['--internal'] if internal else []),name])

def volume(name, image):
    if inspect('volume',name): return
    run(['docker','volume','create','--label',f'tc.owner={OWNER}',name])
    run(['docker','run','--rm','--user','0:0','--network','none','--entrypoint','sh',
         '--mount',f'type=volume,src={name},dst=/owned',image,'-c','chown 10001:10001 /owned && chmod 700 /owned'])

def ensure_redis(c, base):
    r=c['redis']; name=r['container_name']; net=r['network']; old=inspect('container',name)
    if old:
        # Only explicit selection is reused. Never adopt/delete an arbitrary Redis by scanning names.
        need(old['Config']['Image'].startswith('redis:'),'EXISTING_REDIS_IMAGE_REVIEW_REQUIRED')
        need(old['HostConfig']['NetworkMode']!='host' and not old['HostConfig'].get('PortBindings'),'REDIS_HAS_PUBLIC_PORTS')
        need(net in old['NetworkSettings']['Networks'],'EXISTING_REDIS_NETWORK_REVIEW_REQUIRED')
        network(net,True)
        ns=old['Config'].get('Labels',{}).get('tc.redis.namespace')
        need(ns is None or ns==r['namespace'],'REDIS_NAMESPACE_MISMATCH')
        if not old['State']['Running']: run(['docker','start',name])
    else:
        # Refuse a second cache if the previous Compose-managed cache still exists under another name.
        names=run(['docker','ps','-a','--filter','label=com.docker.compose.service=chat-redis','--format','{{.Names}}']).strip()
        need(not names,'EXISTING_COMPOSE_REDIS_SELECT_IT_IN_RUNTIME_CONFIG')
        run(['docker','pull',REDIS_IMAGE],timeout=300)
        network(net,True); volume('translacat-chat-redis-data',REDIS_IMAGE)
        rd=base/'redis'; need(not rd.exists(),'REDIS_DIRECTORY_EXISTS_REVIEW_REQUIRED');rd.mkdir(mode=0o700);os.chown(rd,10001,10001)
        commands='+ping +echo +hello +quit +select +info +client|setname +client|setinfo +get +exists +del +psetex +pexpire +pttl +zadd +zrem +zremrangebyscore +zcard +eval +evalsha +script|load +script|exists +publish +subscribe +unsubscribe'
        ph=hashlib.sha256(r['password'].encode()).hexdigest()
        write(rd/'users.acl',f"user default reset off\nuser chat reset on #{ph} ~{r['namespace']}:* &{r['namespace']}:* -@all {commands}\n")
        write(rd/'redis.conf','bind 0.0.0.0\nport 6379\nprotected-mode yes\ndatabases 1\naclfile /run/config/users.acl\nmaxmemory 96mb\nmaxmemory-policy noeviction\nappendonly yes\nappendfsync everysec\nsave ""\ndir /data\n')
        run(['docker','run','-d','--name',name,'--label',f'tc.owner={OWNER}','--label',f"tc.redis.namespace={r['namespace']}",
            '--user','10001:10001','--read-only','--cap-drop','ALL','--security-opt','no-new-privileges:true',
            '--memory','160m','--pids-limit','64','--restart','unless-stopped','--network',net,
            '--mount',f'type=bind,src={rd},dst=/run/config,readonly',
            '--mount','type=volume,src=translacat-chat-redis-data,dst=/data',REDIS_IMAGE,'redis-server','/run/config/redis.conf'])
    # No password in argv; PING only. No FLUSHALL, ACL SETUSER or container recreation.
    for _ in range(10):
        try:
            out=run(['docker','exec','-i',name,'sh','-c',
                'IFS= read -r REDISCLI_AUTH; export REDISCLI_AUTH; exec redis-cli --user chat --no-auth-warning --raw PING'],data=r['password']+'\n')
            need(out.strip()=='PONG','REDIS_AUTH_FAILED'); return
        except DeployError: time.sleep(1)
    raise DeployError('REDIS_PING_FAILED_EXISTING_REDIS_PRESERVED')

def base_args(c, cfg, name, image, *, migration=False):
    s=c['service']; net='translacat-'+s+'-egress'
    args=['docker','create','--name',name,'--label',f'tc.owner={OWNER}','--label',f'tc.service={s}',
        '--init','--user','10001:10001','--read-only','--cap-drop','ALL','--security-opt','no-new-privileges:true',
        '--pids-limit','192','--memory','640m' if s=='ll' else '512m','--cpus','1',
        '--tmpfs','/tmp:rw,nosuid,size=96m,mode=1777','--network',net,
        '--mount',f'type=bind,src={cfg},dst=/run/config,readonly']
    if migration:
        if s=='chat': args+=['-e','CHAT_ALLOW_MIGRATIONS=true','-e','CHAT_MIGRATION_CONNECTION_FILE=/run/config/db_connection']
        args+=[image,'--migrate-database']
    else:
        args+=['--restart','unless-stopped','--log-opt','max-size=10m','--log-opt','max-file=3',
            '-p','127.0.0.1:8081:8081' if s=='ll' else '127.0.0.1:5085:8080']
        if s=='ll': args+=['--mount','type=volume,src=translacat-ll-data,dst=/app/data']
        args+=[image]
    return args

def wait_ready(service, seconds=300):
    url='http://127.0.0.1:8081/health/ready' if service=='ll' else 'http://127.0.0.1:5085/api/ready'
    end=time.monotonic()+seconds
    while time.monotonic()<end:
        try:
            with urlopen(url,timeout=4) as r:
                if r.status==200 and json.load(r).get('status')=='READY': return True
        except Exception: pass
        time.sleep(3)
    return False

def assert_vm_role(c):
    # IMDS read only; abort before migrations if Secrets select the wrong OCI VM.
    try:
        req=Request('http://169.254.169.254/opc/v2/vnics/',headers={'Authorization':'Bearer Oracle'})
        with urlopen(req,timeout=5) as r: vnics=json.load(r)
        ips={row.get('privateIp') for row in vnics}
    except Exception: raise DeployError('VM_IDENTITY_CHECK_FAILED') from None
    need(c['expected_vm_private_ip'] in ips,'VM_ROLE_PRIVATE_IP_MISMATCH')

def external_preflight(c, cfg):
    # TLS and remote listener proof only. Authentication/business calls remain a separate E2E gate.
    ca=ssl.create_default_context(cafile=str(cfg/'ca-bundle.pem'))
    for key in ('ai_url', *(['be_url'] if c['service']=='chat' else [])):
        u=urlparse(c[key]); import socket
        try:
            with socket.create_connection((u.hostname,u.port or 443),timeout=8) as sock:
                with ca.wrap_socket(sock,server_hostname=u.hostname): pass
        except Exception: raise DeployError('REMOTE_HTTPS_PREFLIGHT_FAILED:'+key) from None

def deploy(c, image, sha, release_id, token_path):
    need(os.geteuid()==0,'RUN_WITH_SUDO_REQUIRED')
    need(re.fullmatch('[0-9a-f]{40}',sha) is not None,'INVALID_SHA')
    need(re.fullmatch(r'ghcr\.io/[a-z0-9_.-]+/[a-z0-9_.-]+@sha256:[0-9a-f]{64}',image) is not None,'IMMUTABLE_IMAGE_REQUIRED')
    need(re.fullmatch('[0-9a-f]{40}-[0-9]+-[0-9]+',release_id) is not None,'INVALID_RELEASE_ID')
    base=ROOT/c['service']; base.mkdir(parents=True,exist_ok=True,mode=0o700)
    with open(base/'deploy.lock','a') as lock:
        try: fcntl.flock(lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
        except BlockingIOError: raise DeployError('ANOTHER_DEPLOYMENT_IS_RUNNING') from None
        os.chmod(base,0o700)
        release=base/'releases'/release_id
        need(not release.exists(),'RELEASE_ID_ALREADY_EXISTS');release.mkdir(parents=True,mode=0o700)
        cfg=release/'runtime'; mig=release/'migration'; docker_config=release/'registry'
        env=dict(os.environ,DOCKER_CONFIG=str(docker_config))
        migration_done=False; previous=None; current='translacat-'+c['service']; backup=current+'-previous-'+release_id[-25:]
        migration_name=current+'-migration-'+release_id[-25:]
        created_new=False
        try:
            materialize(c,release); materialize(c,release,True)
            docker_config.mkdir(mode=0o700)
            assert_vm_role(c)
            external_preflight(c,cfg)
            token=token_path.read_text(); need(token.strip(),'REGISTRY_TOKEN_MISSING')
            run(['docker','login','ghcr.io','-u',c['registry_user'],'--password-stdin'],data=token,env=env)
            token=''
            run(['docker','pull',image],timeout=600,env=env)
            actual=inspect('image',image)
            need(actual and actual['Config'].get('Labels',{}).get('org.opencontainers.image.revision')==sha,'IMAGE_SHA_LABEL_MISMATCH')
            network('translacat-'+c['service']+'-egress')
            if c['service']=='chat': ensure_redis(c,base)
            else: volume('translacat-ll-data',image)
            previous=inspect('container',current)
            if previous: need(previous['Config'].get('Labels',{}).get('tc.owner')==OWNER,'EXISTING_APPLICATION_REQUIRES_EXPLICIT_ADOPTION')
            # A separate container receives only DDL credentials. Existing app remains up during migration.
            run(base_args(c,mig,migration_name,image,migration=True))
            run(['docker','start',migration_name])
            try: code=run(['docker','wait',migration_name],timeout=960).strip()
            except DeployError:
                run(['docker','stop','-t','15',migration_name]); raise
            need(code=='0','MIGRATION_FAILED:CHECK_PRIVILEGES_OR_SCHEMA;NO_DATABASE_ROLLBACK')
            migration_done=True
            run(['docker','rm',migration_name])
            shutil.rmtree(mig)
            if previous:
                run(['docker','stop','-t','30',current]);run(['docker','rename',current,backup])
            run(base_args(c,cfg,current,image));created_new=True
            if c['service']=='chat':
                # Attach before starting the process, so Redis resolves at application initialization.
                run(['docker','network','connect',c['redis']['network'],current])
            run(['docker','start',current])
            need(wait_ready(c['service']),'APPLICATION_READINESS_FAILED')
            # Check TLS edge/proxy end to end, not just the localhost app.
            u=c['public_url'].rstrip('/')+('/health/ready' if c['service']=='ll' else '/api/ready')
            run(['curl','--fail','--silent','--show-error','--connect-timeout','5','--max-time','15',
                '--cacert',str(cfg/'service-ca.pem'),'--resolve',f'{urlparse(u).hostname}:8443:127.0.0.1',u])
            (release/'result.json').write_text(json.dumps({'status':'DEPLOYED_READY','sha':sha,'image':image,
                'service':c['service'],'previous_container':backup if previous else None,
                'business_e2e':'NOT_RUN','database_rollback':'NEVER_AUTOMATIC'},indent=2))
            print('DEPLOYED_READY: '+c['service']+' '+sha+'; BUSINESS_E2E_NOT_RUN')
        except Exception:
            if created_new:
                try: run(['docker','stop','-t','30',current]);run(['docker','rename',current,current+'-failed-'+release_id[-25:]])
                except DeployError: pass
            if previous and not inspect('container',backup) and not created_new:
                # stop succeeded but rename failed: never strand the original container silently.
                original=inspect('container',current)
                if original and original.get('Id')==previous.get('Id') and previous['State']['Running']:
                    if not migration_done or c['allow_app_rollback_after_migration']:
                        run(['docker','start',current])
                    else: print('ORIGINAL_CONTAINER_PRESERVED; REVIEW_MIGRATION_BEFORE_RESTART',file=sys.stderr)
            if previous and inspect('container',backup):
                if not migration_done or c['allow_app_rollback_after_migration']:
                    run(['docker','rename',backup,current])
                    if previous['State']['Running']:
                        run(['docker','start',current])
                        if not wait_ready(c['service'],seconds=60):
                            print('PREVIOUS_CONTAINER_RESTORED_BUT_NOT_READY; MANUAL_RECOVERY_REQUIRED',file=sys.stderr)
                        else: print('PREVIOUS_CONTAINER_RESTORED_AND_READY; DATABASE_NOT_ROLLED_BACK',file=sys.stderr)
                    else: print('PREVIOUS_CONTAINER_RESTORED_STOPPED_AS_BEFORE',file=sys.stderr)
                else: print('PREVIOUS_CONTAINER_PRESERVED_STOPPED; MIGRATION_COMPATIBILITY_REVIEW_REQUIRED',file=sys.stderr)
            raise
        finally:
            shutil.rmtree(docker_config,ignore_errors=True)
            # A failed migration must not keep DDL credentials indefinitely.
            if inspect('container',migration_name):
                try: run(['docker','stop','-t','15',migration_name]);run(['docker','rm',migration_name])
                except DeployError: pass
            shutil.rmtree(mig,ignore_errors=True)
            if token_path.exists(): token_path.unlink()

def main():
    p=argparse.ArgumentParser();p.add_argument('--service',choices=['ll','chat'],required=True)
    p.add_argument('--config',type=Path,required=True);p.add_argument('--image',required=True)
    p.add_argument('--sha',required=True);p.add_argument('--release-id',required=True);p.add_argument('--registry-token',type=Path,required=True)
    a=p.parse_args()
    def interrupted(signum, frame):
        raise DeployError('DEPLOYMENT_INTERRUPTED')
    for sig in (signal.SIGTERM,signal.SIGHUP,signal.SIGINT): signal.signal(sig,interrupted)
    try:
        c=validate(json.loads(a.config.read_text()),a.service)
        origin(c['public_url'],'public_url');need(urlparse(c['public_url']).port==8443,'PUBLIC_HTTPS_PORT_MUST_BE_8443');one_line(c['registry_user'],'registry_user')
        deploy(c,a.image,a.sha,a.release_id,a.registry_token)
    except (DeployError,KeyError,ValueError,TypeError,OSError) as error:
        print('DEPLOYMENT_FAILED: '+(str(error) if isinstance(error,DeployError) else type(error).__name__),file=sys.stderr)
        return 1
    finally:
        # Uploaded raw config is disposable; retained runtime files are access-restricted.
        if a.config.exists(): a.config.unlink()
    return 0
if __name__=='__main__': sys.exit(main())
