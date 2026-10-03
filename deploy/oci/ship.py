#!/usr/bin/env python3
"""GitHub runner -> optional BE ProxyJump -> OCI. No secret is interpolated into a shell script."""
import argparse, ipaddress, json, os, re, shlex, shutil, subprocess, sys, tempfile
from pathlib import Path
from urllib.parse import urlparse

def checked(argv, *, env=None, timeout=1800, input=None, show=False):
    p=subprocess.run(argv,env=env,input=input,text=True,capture_output=not show,timeout=timeout)
    if p.returncode: raise RuntimeError('transport command failed: '+argv[0]+' exit='+str(p.returncode))
    return '' if show else p.stdout

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--service',choices=['ll','chat'],required=True)
    ap.add_argument('--image',required=True);a=ap.parse_args()
    sha=os.environ['GITHUB_SHA'];runid=os.environ['GITHUB_RUN_ID'];attempt=os.environ['GITHUB_RUN_ATTEMPT']
    if not re.fullmatch('[0-9a-f]{40}',sha) or not runid.isdigit() or not attempt.isdigit(): raise RuntimeError('invalid release identity')
    if not re.fullmatch(r'ghcr\.io/[a-z0-9_.-]+/[a-z0-9_.-]+@sha256:[0-9a-f]{64}',a.image): raise RuntimeError('immutable image required')
    host=str(ipaddress.IPv4Address(os.environ['OCI_HOST'])); user=os.environ.get('OCI_USERNAME','ubuntu')
    if user!='ubuntu': raise RuntimeError('this package requires reviewed ubuntu sudo access')
    release=f'{sha}-{runid}-{attempt}';remote=f'/home/ubuntu/.local/translacat-incoming/{release}'
    env=dict(os.environ);agent_pid=None
    with tempfile.TemporaryDirectory(prefix='tc-release-') as tmp:
        tmp=Path(tmp);os.chmod(tmp,0o700)
        def secret(name,value):
            p=tmp/name;p.write_text(value,encoding='utf-8');p.chmod(0o600);return p
        config=json.loads(os.environ['RUNTIME_CONFIG_JSON'])
        if config.get('service')!=a.service: raise RuntimeError('runtime service mismatch')
        if urlparse(config.get('public_url','')).hostname!=host: raise RuntimeError('runtime public URL must match OCI_HOST')
        # Registry identity is derived from this workflow, not from an uploaded password.
        config['registry_user']=os.environ['GITHUB_ACTOR']
        runtime=secret('runtime.json',json.dumps(config,ensure_ascii=False))
        token=secret('registry-token',os.environ['GHCR_TOKEN'])
        secret('known_hosts',os.environ['SSH_KNOWN_HOSTS'].strip()+'\n')
        if 'PRIVATE KEY' in os.environ['SSH_KNOWN_HOSTS']: raise RuntimeError('known_hosts must contain server public host keys only')
        key=secret('target-key',os.environ['SSH_PRIVATE_KEY'].rstrip()+'\n')
        ask=tmp/'askpass.sh';ask.write_text('#!/bin/sh\nprintf "%s\\n" "$TC_KEY_PASSPHRASE"\n');ask.chmod(0o700)
        out=checked(['ssh-agent','-s'])
        env['SSH_AUTH_SOCK']=re.search(r'SSH_AUTH_SOCK=([^;]+);',out).group(1)
        agent_pid=int(re.search(r'SSH_AGENT_PID=(\d+);',out).group(1))
        env.update(SSH_ASKPASS=str(ask),SSH_ASKPASS_REQUIRE='force',DISPLAY='unused:0',TC_KEY_PASSPHRASE=os.environ.get('SSH_PASSPHRASE',''))
        try:
            checked(['ssh-add',str(key)],env=env,timeout=30,input='')
            # Separate target and jump keys. Agent forwarding is never enabled.
            common=f'''  User ubuntu
  Port 22
  BatchMode yes
  IdentitiesOnly yes
  StrictHostKeyChecking yes
  UserKnownHostsFile {tmp}/known_hosts
  GlobalKnownHostsFile /dev/null
  UpdateHostKeys no
  ForwardAgent no
  ConnectTimeout 15
  ServerAliveInterval 20
  ServerAliveCountMax 3
'''
            text=f'Host target\n  HostName {host}\n  IdentityFile {key}\n'+common
            jump=os.environ.get('BE_JUMP_HOST','').strip()
            if jump:
                jump=str(ipaddress.IPv4Address(jump));jk=secret('jump-key',os.environ['BE_JUMP_PRIVATE_KEY'].rstrip()+'\n')
                env['TC_KEY_PASSPHRASE']=os.environ.get('BE_JUMP_PASSPHRASE','')
                checked(['ssh-add',str(jk)],env=env,timeout=30,input='')
                text+='  ProxyJump jump\nHost jump\n  HostName '+jump+'\n  IdentityFile '+str(jk)+'\n'+common
            secret('ssh_config',text)
            ssh=['ssh','-F',str(tmp/'ssh_config')]
            checked(ssh+['target','umask 077; mkdir -p '+remote],env=env,timeout=45)
            checked(['scp','-q','-F',str(tmp/'ssh_config'),str(runtime),str(token),'deploy/oci/deploy.py','target:'+remote+'/'],env=env,timeout=60)
            command=['sudo','-n','python3',remote+'/deploy.py','--service',a.service,'--config',remote+'/runtime.json',
                '--image',a.image,'--sha',sha,'--release-id',release,'--registry-token',remote+'/registry-token']
            # deploy.py only emits allowlisted diagnostics; no remote docker/SQL output is streamed.
            checked(ssh+['target',shlex.join(command)],env=env,timeout=2400,show=True)
        finally:
            try: checked(['ssh','-F',str(tmp/'ssh_config'),'target','rm -f '+remote+'/runtime.json '+remote+'/registry-token'],env=env,timeout=30)
            except Exception: pass
            if agent_pid:
                try: os.kill(agent_pid,15)
                except ProcessLookupError: pass
    return 0
if __name__=='__main__':
    try: sys.exit(main())
    except Exception as e:
        # An exception object can contain SSH arguments; report its class, not secret-bearing input.
        print('RELEASE_TRANSPORT_FAILED: '+type(e).__name__+'; check SSH path/key/known_hosts and deployment status.',file=sys.stderr)
        sys.exit(1)
