﻿using Neo.Lux.Core;
using Neo.Lux.Cryptography;
using Neo.Lux.Debugger;
using Neo.Lux.Utils;
using Neo.Lux.VM;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Phantasma.Bridge.Core
{
    public class Mailbox
    {
        public readonly string name;
        public readonly UInt160 address;

        public Mailbox(string name, UInt160 address)
        {
            this.name = name;
            this.address = address;
        }

        public static bool ValidateMailboxName(byte[] mailbox_name)
        {
            if (mailbox_name.Length <= 4 || mailbox_name.Length >= 20)
                return false;

            int index = 0;
            while (index < mailbox_name.Length)
            {
                var c = mailbox_name[index];
                index++;

                if (c >= 97 && c <= 122) continue; // lowercase allowed
                if (c == 95) continue; // underscore allowed
                if (c >= 48 && c <= 57) continue; // numbers allowed

                return false;
            }

            return true;
        }
    }

    public class BridgeManager : IBlockchainProvider
    {
        private NeoAPI neo_api;
        private byte[] contract_bytecode;
        private UInt160 contract_hash;
        private bool running;

        private SnapshotVM listenerVM;

        private Dictionary<UInt256, Transaction> transactions = new Dictionary<UInt256, Transaction>();

        private Dictionary<UInt160, Mailbox> addressMap = new Dictionary<UInt160, Mailbox>();
        private Dictionary<string, Mailbox> nameMap = new Dictionary<string, Mailbox>();

        public BridgeManager(NeoAPI api, Transaction deployTx, uint lastBlock)
        {
            this.neo_api = api;
            this.lastBlock = lastBlock;

            if (deployTx != null)
            {
                var code = NeoTools.Disassemble(deployTx.script);
                

                for (int i= 1; i<code.Count; i++)
                {
                    var entry = code[i];
                    if (entry.opcode == OpCode.SYSCALL && entry.data != null)
                    {
                        var method = Encoding.ASCII.GetString(entry.data);

                        if (method== "Neo.Contract.Create")
                        {
                            var prev = code[i - 1];
                            this.contract_bytecode = prev.data;
                            this.contract_hash = contract_bytecode.ToScriptHash();
                            break;
                        }
                    }
                }

            }
            else
            {
                throw new Exception($"Invalid deploy transaction");
            }

            this.listenerVM = new SnapshotVM(this);
        }

        public void Stop()
        {
            if (running)
            {
                running = false;
            }
        }

        private uint lastBlock;

        public void Run()
        {
            if (running)
            {
                return;
            }

            this.running = true;

            do
            {
                var currentBlock = neo_api.GetBlockHeight();
                if (currentBlock > lastBlock)
                {
                    while (lastBlock < currentBlock)
                    {
                        lastBlock++;
                        ProcessIncomingBlock(lastBlock);
                    }
                }

                // sleeps 10 seconds in order to wait some time until next block is generated
                Thread.Sleep(20 * 1000);

            } while (running);
        }

        private void ProcessIncomingBlock(uint height)
        {
            Console.WriteLine("Processing block #" + height);

            var block = neo_api.GetBlock(height);

            if (block == null)
            {
                throw new Exception($"API failure, could not fetch block #{height}");
            }

            foreach (var tx in block.transactions)
            {
                if (tx.type != TransactionType.InvocationTransaction)
                {
                    continue;
                }

                List<AVMInstruction> code;

                try
                {
                    code = NeoTools.Disassemble(tx.script);
                }
                catch
                {
                    continue;
                }

                for (int i = 1; i < code.Count; i++)
                {
                    var op = code[i];

                    // opcode data must contain the script hash to the Phantasma contract, otherwise ignore it
                    if (op.opcode == OpCode.APPCALL && op.data != null && op.data.Length == 20)
                    {
                        var scriptHash = new UInt160(op.data);

                        if (scriptHash != contract_hash)
                        {
                            continue;
                        }

                        var prev = code[i - 1];
                        if (prev.data == null)
                        {
                            continue;
                        }

                        var method = Encoding.ASCII.GetString(prev.data);

                        int index = i - 3;
                        var argCount = (byte)code[index].opcode - (byte)OpCode.PUSH0;
                        var args = new List<byte[]>();

                        while (argCount > 0)
                        {
                            index--;
                            args.Add(code[index].data);
                            argCount--;
                        }

                        switch (method)
                        {
                            case "registerMailbox":
                                {
                                    var address = new UInt160(args[0]);
                                    var name = Encoding.ASCII.GetString(args[1]);

                                    string result;

                                    if (addressMap.ContainsKey(address))
                                    {
                                        result = "Address already has a box";
                                    }
                                    else if (nameMap.ContainsKey(name))
                                    {
                                        result = "Box name already exists";
                                    }
                                    else
                                    if (!Mailbox.ValidateMailboxName(args[1]))
                                    {
                                        result = "Box name is invalid";
                                    }
                                    else
                                    {
                                        result = "OK";
                                    }

                                    Console.WriteLine($"{method} ({address.ToAddress()}, {name}) => {result}");

                                    break;
                                }
                        }
                    }
                }

            }

        }

        /// <summary>
        /// Fetches a transaction from local catch. If not found, will try fetching it from a NEO blockchain node
        /// </summary>
        /// <param name="hash">Hash of the transaction</param>
        /// <returns></returns>
        public Transaction GetTransaction(UInt256 hash)
        {
            return transactions.ContainsKey(hash) ? transactions[hash] : neo_api.GetTransaction(hash);
        }
    }
}
