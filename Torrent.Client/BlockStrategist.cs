﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Torrent.Client.Events;
using Torrent.Client.Extensions;

namespace Torrent.Client
{
    public class BlockStrategist
    {
        private readonly int blockSize;
        private readonly int pieceSize;
        private readonly long totalSize;
        private readonly int blockCount;
        private readonly BlockAddressCollection<int> unavailable;
        private readonly int[] pieces;

        public BitArray Bitfield { get; private set; }
        public int Available { get; private set; }
        public bool Complete
        {
            get
            {
                lock (unavailable)
                    return !unavailable.Any();
            }
        }

        public BlockStrategist(TorrentData data)
        {
            this.blockSize = Global.Instance.BlockSize;
            pieceSize = data.PieceLength;
            totalSize = data.Files.Sum(f => f.Length);
            blockCount = (int)Math.Ceiling((float)totalSize/blockSize);
            pieces = new int[data.PieceCount];
            Bitfield = new BitArray(data.PieceCount);
            unavailable = new BlockAddressCollection<int>();
            for (int i = 0; i < blockCount; i++)
                unavailable.Add(i);

            for(int i = 0; i < data.PieceCount-1; i++)
            {
                pieces[i]= data.PieceLength;
            }
            int lastLength = (int)(data.TotalLength - (data.PieceLength*(data.PieceCount - 1)));
            pieces[pieces.Length - 1] = lastLength;
        }

        public BlockInfo Next(BitArray bitfield)
        {
            if (Available == blockCount)
                return BlockInfo.Empty;
            BlockInfo block;
            int counter = 0;
            do
            {   //увеличаваме брояча на опитите за намиране на произволен блок
                counter++;
                int index;
                lock(unavailable)
                {   //ако има липсващи блокове, избираме случаен от тях
                    if (unavailable.Any())
                        index = unavailable.Random();
                    else return BlockInfo.Empty; //в противен случай, връщаме празен
                }
                //преобразуване на адрес
                block = Block.FromAbsoluteAddress((long)index*blockSize, pieceSize, blockSize,
                                                 totalSize);
                if (counter > 10) //ако броячът за опитите надвиши 10, връщаме блока, независимо дали пиъра го има
                    return block;
                //повторяме докато не установим, че bitfield-а на пиъра съдържа произволно избрания блок
            } while (!bitfield[block.Index]);
            return block;
        }

        public bool Received(BlockInfo block)
        {   //изчисляване на адреса на блока
            int address = (int)(Block.GetAbsoluteAddress(block.Index, block.Offset, pieceSize)/blockSize);
            lock (unavailable)
            {   //ако колекцията с липсващи блокове включва адреса на блока, и блока е с дължина
                //по-голяма от 0, тогава го обработваме
                if(unavailable.Contains(address) && block.Length > 0)
                {   //премахване на новия блок от колекцията със липсващи
                    Debug.WriteLine("Needed block incoming:" + address);
                    unavailable.Remove(address);
                    Available++; //увеличаване на броя на наличните блокове
                    pieces[block.Index] -= block.Length; //изваждане на размера на блока от размера на парчето, което го съдържа
                    if(pieces[block.Index]<=0) //ако парчето, съдържащо блока, има размер 0, тогава обявяваме парчето за свалено
                    {
                        SetDownloaded(block.Index);
                    }
                    return true;
                }
                Debug.WriteLine("Unneeded block incoming:" + address);
                return false;
            }
        }

        private void SetDownloaded(int piece)
        {
            Bitfield.Set(piece, true);
            OnHavePiece(piece);
        }

        public event EventHandler<EventArgs<int>> HavePiece;

        private void OnHavePiece(int e)
        {
            EventHandler<EventArgs<int>> handler = HavePiece;
            if(handler != null) handler(this, new EventArgs<int>(e));
        }
    }

    public class BlockAddressCollection<T>:KeyedCollection<int,int>
    {
        protected override int GetKeyForItem(int item)
        {
            return item;
        }
    }
}